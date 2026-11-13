using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Credentials;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Fares;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Tickets;

namespace TransitPass.Core.Api.Gates;

/// <summary>What a gate sends for one tap. Nullable fields so a missing one is a clear 400.</summary>
public sealed record GateTapRequest(Guid? TapId, string? GateId, string? StationId, string? Direction, string? Credential);

/// <summary>
/// The decision about one tap, stored in gate_taps.response and replayed for a resent tap id.
/// <see cref="Status"/> is the HTTP status: 200 when the gate opens, 409/422 when it stays closed.
/// </summary>
public sealed record GateDecision(
    Guid TapId, bool Allowed, int Status, string Code, string Message, string Direction,
    Guid TicketId, string Station, string TicketStatus, long SurchargePoisha);

/// <summary>A decision, and whether it is the stored answer to an earlier tap with the same id.</summary>
public sealed record GateOutcome(GateDecision Decision, bool Replayed);

/// <summary>A validated tap from an authenticated gate.</summary>
internal sealed record GateTapCommand(Guid TapId, GateDevice Gate, GateDirection Direction, string Credential);

/// <summary>
/// Entry and exit. The rules:
/// <list type="bullet">
/// <item>Network calls (station check, fare) happen BEFORE the transaction: a slow network must never hold row locks.</item>
/// <item>Every status change is a guarded UPDATE (compare-and-set): of two gates scanning one QR at the same moment,
/// Postgres lets exactly one move the ticket; the other sees 0 rows and is refused.</item>
/// <item>Locks are always taken in the same order, ticket → journey → wallet (cancel: ticket → wallet), so two
/// transactions can never each hold what the other waits for: no deadlock cycle can form.</item>
/// <item>Entry is allowed at ANY station. The exit prices the trip actually made (entry station → exit station);
/// if that costs more than the ticket's fare, the difference is charged (a Surcharge ledger row). Shorter trips
/// get no refund (the metro's standard rule).</item>
/// </list>
/// </summary>
internal static class GateTaps
{
    internal const string Entered = "gate.entered";
    internal const string Exited = "gate.exited";
    internal const int MaxCredentialLength = TicketCredentialVerifier.MaxLength;

    /// <summary>Checks the body (the gate is already authenticated). Cheap checks only; nothing here touches the database.</summary>
    internal static Result<GateTapCommand> Validate(GateTapRequest body, GateDevice gate)
    {
        if (body.TapId is not Guid tapId || tapId == Guid.Empty)
        {
            return Error.Validation("gate.invalid_request", "'tapId' (a UUID the gate makes for each tap) is required.");
        }

        GateDirection? direction = body.Direction?.ToUpperInvariant() switch
        {
            "ENTRY" => GateDirection.Entry,
            "EXIT" => GateDirection.Exit,
            _ => null,
        };
        if (direction is not GateDirection dir)
        {
            return Error.Validation("gate.invalid_request", "'direction' must be \"entry\" or \"exit\".");
        }

        if (string.IsNullOrWhiteSpace(body.Credential) || string.IsNullOrWhiteSpace(body.StationId))
        {
            return Error.Validation("gate.invalid_request", "'stationId' and 'credential' are required.");
        }

        // The station comes from the gate's enrollment. A gate claiming another station is refused, so a
        // stolen key for a cheap station can't be used to fake exits somewhere else.
        if (!string.Equals(body.StationId, gate.Station, StringComparison.Ordinal))
        {
            return Error.Forbidden("gate.wrong_station", "This gate is not enrolled at that station.");
        }

        return new GateTapCommand(tapId, gate, dir, body.Credential);
    }

    /// <summary>1. seen this tap id? replay → 2. verify the QR → 3. entry or exit.</summary>
    internal static async Task<Result<GateOutcome>> TapAsync(
        GateTapCommand tap, CoreDbContext db, TicketCredentialVerifier verifier, IFareQuotes fares,
        IStationDirectory stations, GateOptions options, TimeProvider clock, CancellationToken ct)
    {
        string hash = Hash(tap);

        // 1. A resent tap (the gate timed out but we had answered): the same decision, nothing changes twice.
        GateTap? seen = await FindAsync(db, tap.TapId, ct);
        if (seen is not null)
        {
            return Replay(seen, hash);
        }

        // 2. Genuine and in date? (Public-key check, no database.) Exits get a grace period after midnight.
        var verified = await verifier.VerifyAsync(
            tap.Credential, tap.Direction == GateDirection.Exit ? options.ExitGracePeriod : TimeSpan.Zero);
        if (!verified.IsSuccess)
        {
            // The gate itself is authenticated (that was the 401); it's the passenger's QR that is refused → 403.
            return Error.Forbidden(verified.Error.Code, verified.Error.Message);
        }

        // 3. The signature says "genuine"; only the database knows "unused". Entry/Exit check that under a row lock.
        return tap.Direction == GateDirection.Entry
            ? await EnterAsync(tap, hash, verified.Value, db, stations, clock, ct)
            : await ExitAsync(tap, hash, verified.Value, db, fares, clock, ct);
    }

    private static async Task<Result<GateOutcome>> EnterAsync(
        GateTapCommand tap, string hash, TicketCredentialClaims claims, CoreDbContext db, IStationDirectory stations,
        TimeProvider clock, CancellationToken ct)
    {
        // Outside the transaction: does the gate's station exist in the network? (Config could name a closed station.)
        var station = await stations.EnsureExistsAsync(tap.Gate.Station, ct);
        if (!station.IsSuccess)
        {
            return station.Error;
        }

        DateTimeOffset now = clock.GetUtcNow();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Lock 1: the ticket. The guard and the change in one statement. A second gate (or a cancel) doing the
            // same UPDATE waits for our COMMIT, re-checks "status = 'Active'", finds 'InJourney' and matches 0 rows.
            List<Guid> moved = await db.Database
                .SqlQuery<Guid>($"""
                    UPDATE tickets
                       SET status = 'InJourney'
                     WHERE id = {claims.TicketId}
                       AND user_id = {claims.UserId}
                       AND status = 'Active'
                       AND expires_at > {now}
                    RETURNING id AS "Value"
                    """)
                .ToListAsync(ct);

            if (moved.Count == 1)
            {
                // Lock 2: the journey (a new row; UNIQUE ticket_id makes a second journey impossible anyway).
                db.Journeys.Add(new Journey
                {
                    Id = Guid.CreateVersion7(now),
                    TicketId = claims.TicketId,
                    EntryStation = tap.Gate.Station,
                    EntryTapId = tap.TapId,
                    EnteredAt = now,
                    Status = JourneyStatus.InProgress,
                });
                await db.SaveChangesAsync(ct);

                var opened = Decision(tap, StatusCodes.Status200OK, Entered, "Welcome. Your journey has started.",
                    claims.TicketId, TicketStatus.InJourney, 0);
                return await CommitWithTapAsync(db, tx, tap, hash, opened, now, ct);
            }

            await tx.RollbackAsync(ct);
        }

        // 0 rows: read the ticket once to tell the gate why.
        Ticket? ticket = await ReadTicketAsync(db, claims, ct);
        if (ticket is null)
        {
            return TicketNotFound;
        }

        var refused = ticket.Status switch
        {
            TicketStatus.InJourney => Decision(tap, StatusCodes.Status409Conflict, "gate.already_inside",
                "This ticket is already inside. It can't enter twice.", ticket),
            TicketStatus.Active => Decision(tap, StatusCodes.Status409Conflict, "ticket.expired",
                "This ticket's service day is over.", ticket),
            _ => Decision(tap, StatusCodes.Status409Conflict, "ticket.not_usable",
                $"A ticket that is {ticket.Status} cannot be used at a gate.", ticket),
        };
        return await RecordRefusalAsync(db, tap, hash, refused, now, ct);
    }

    private static async Task<Result<GateOutcome>> ExitAsync(
        GateTapCommand tap, string hash, TicketCredentialClaims claims, CoreDbContext db, IFareQuotes fares,
        TimeProvider clock, CancellationToken ct)
    {
        // Read first, without locks: we need the entry station to ask Network for the fare, and that HTTP call
        // must happen before any lock is taken.
        Ticket? ticket = await ReadTicketAsync(db, claims, ct);
        if (ticket is null)
        {
            return TicketNotFound;
        }

        if (ticket.Status != TicketStatus.InJourney)
        {
            return await RecordRefusalAsync(db, tap, hash, ExitRefusal(tap, ticket), clock.GetUtcNow(), ct);
        }

        // InJourney ⇒ the journey exists: both are written in the same entry transaction. A ticket moved to
        // InJourney by hand (no entry tap on record) has no entry station to price from: refuse, don't crash.
        string? entryStation = await db.Journeys.AsNoTracking()
            .Where(j => j.TicketId == ticket.Id).Select(j => j.EntryStation).SingleOrDefaultAsync(ct);
        if (entryStation is null)
        {
            var noEntry = Decision(tap, StatusCodes.Status409Conflict, "gate.not_inside",
                "No entry is on record for this ticket. Please ask station staff.", ticket);
            return await RecordRefusalAsync(db, tap, hash, noEntry, clock.GetUtcNow(), ct);
        }

        // The trip actually made. Leaving where you came in costs nothing extra (and needs no fare).
        long tripFare = 0;
        if (!string.Equals(entryStation, tap.Gate.Station, StringComparison.Ordinal))
        {
            var quote = await fares.QuoteAsync(entryStation, tap.Gate.Station, ct);
            if (!quote.IsSuccess)
            {
                return quote.Error; // 503: the gate stays closed, nothing changed, the passenger taps again
            }

            tripFare = quote.Value.FarePoisha;
        }

        long surcharge = Math.Max(0, tripFare - ticket.FarePoisha); // longer trip: pay the difference; shorter: no refund
        DateTimeOffset now = clock.GetUtcNow();

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Lock 1: the ticket. InJourney → Used, only once: a second exit (or a twin gate) matches 0 rows.
            int used = await db.Database.ExecuteSqlAsync($"""
                UPDATE tickets SET status = 'Used'
                 WHERE id = {ticket.Id} AND status = 'InJourney'
                """, ct);

            if (used == 1)
            {
                // Lock 2: the journey. Same entry station as the one we priced (it can't change while InJourney).
                int closed = await db.Database.ExecuteSqlAsync($"""
                    UPDATE journeys
                       SET status = 'Completed', exit_station = {tap.Gate.Station}, exit_tap_id = {tap.TapId},
                           exited_at = {now}, surcharge_poisha = {surcharge}
                     WHERE ticket_id = {ticket.Id} AND status = 'InProgress' AND entry_station = {entryStation}
                    """, ct);
                if (closed != 1)
                {
                    throw new InvalidOperationException($"Ticket {ticket.Id} is InJourney but has no open journey.");
                }

                // Lock 3: the wallet, last. LedgerBook is the only code that moves money (balance + ledger row).
                if (surcharge > 0)
                {
                    Guid walletId = await db.Wallets.Where(w => w.UserId == ticket.UserId).Select(w => w.Id).SingleAsync(ct);
                    var charged = await LedgerBook.PostAsync(db, walletId, -surcharge, LedgerEntryType.Surcharge, ticket.Id, now, ct);
                    if (!charged.IsSuccess)
                    {
                        // Not enough money: undo everything. The ticket stays InJourney and the gate stays closed;
                        // after a top-up the next tap (a new tap id) goes through.
                        await tx.RollbackAsync(ct);
                        var unpaid = Decision(tap, StatusCodes.Status422UnprocessableEntity, "gate.insufficient_funds",
                            $"This trip costs {surcharge} poisha more than the ticket and the wallet can't cover it. Top up, then tap again.",
                            ticket.Id, TicketStatus.InJourney, surcharge);
                        return await RecordRefusalAsync(db, tap, hash, unpaid, now, ct);
                    }
                }

                var opened = Decision(tap, StatusCodes.Status200OK, Exited,
                    surcharge > 0 ? $"Goodbye. {surcharge} poisha was charged for the longer trip." : "Goodbye. Trip complete.",
                    ticket.Id, TicketStatus.Used, surcharge);
                return await CommitWithTapAsync(db, tx, tap, hash, opened, now, ct);
            }

            await tx.RollbackAsync(ct);
        }

        // Someone else moved the ticket between our read and our UPDATE (a twin gate, usually). Say why.
        Ticket current = (await ReadTicketAsync(db, claims, ct))!;
        return await RecordRefusalAsync(db, tap, hash, ExitRefusal(tap, current), now, ct);
    }

    private static GateDecision ExitRefusal(GateTapCommand tap, Ticket ticket) => ticket.Status switch
    {
        TicketStatus.Active => Decision(tap, StatusCodes.Status409Conflict, "gate.not_inside",
            "This ticket has not entered. Tap in at an entry gate first.", ticket),
        TicketStatus.Used => Decision(tap, StatusCodes.Status409Conflict, "gate.already_exited",
            "This ticket's journey is already over.", ticket),
        _ => Decision(tap, StatusCodes.Status409Conflict, "ticket.not_usable",
            $"A ticket that is {ticket.Status} cannot be used at a gate.", ticket),
    };

    /// <summary>
    /// The last statement of an allowed tap: store the decision under the tap id, in the same transaction as the
    /// ticket change. If a twin request with the same tap id got there first, ours is undone and theirs replayed.
    /// </summary>
    private static async Task<Result<GateOutcome>> CommitWithTapAsync(
        CoreDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, GateTapCommand tap, string hash,
        GateDecision decision, DateTimeOffset now, CancellationToken ct)
    {
        if (await InsertTapAsync(db, tap, hash, decision, now, ct))
        {
            await tx.CommitAsync(ct);
            return new GateOutcome(decision, Replayed: false);
        }

        await tx.RollbackAsync(ct);
        return await ReplayStoredAsync(db, tap.TapId, hash, ct);
    }

    /// <summary>A refusal changes nothing, so it is stored on its own (and replayed if the gate resends the tap).</summary>
    private static async Task<Result<GateOutcome>> RecordRefusalAsync(
        CoreDbContext db, GateTapCommand tap, string hash, GateDecision decision, DateTimeOffset now, CancellationToken ct) =>
        await InsertTapAsync(db, tap, hash, decision, now, ct)
            ? new GateOutcome(decision, Replayed: false)
            : await ReplayStoredAsync(db, tap.TapId, hash, ct);

    /// <summary>
    /// INSERT … ON CONFLICT (tap_id) DO NOTHING. If a twin holds the same tap id uncommitted, Postgres makes us
    /// wait for it, then reports 0 rows (it committed) or lets us insert (it rolled back).
    /// </summary>
    private static async Task<bool> InsertTapAsync(
        CoreDbContext db, GateTapCommand tap, string hash, GateDecision decision, DateTimeOffset now, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(decision, JsonSerializerOptions.Web);
        string direction = tap.Direction.ToString();
        int inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO gate_taps (tap_id, gate_id, station, direction, ticket_id, request_hash, allowed, result, response, created_at)
            VALUES ({tap.TapId}, {tap.Gate.Id}, {tap.Gate.Station}, {direction}, {decision.TicketId}, {hash},
                    {decision.Allowed}, {decision.Code}, {json}::jsonb, {now})
            ON CONFLICT (tap_id) DO NOTHING
            """, ct);
        return inserted == 1;
    }

    private static async Task<Result<GateOutcome>> ReplayStoredAsync(CoreDbContext db, Guid tapId, string hash, CancellationToken ct)
    {
        GateTap? stored = await FindAsync(db, tapId, ct);
        return stored is null
            ? Error.Conflict("gate.tap_in_progress", "A tap with this id is still being processed.")
            : Replay(stored, hash);
    }

    private static Result<GateOutcome> Replay(GateTap seen, string hash)
    {
        if (!string.Equals(seen.RequestHash, hash, StringComparison.Ordinal))
        {
            // Same tap id, different gate/QR/direction: a gate bug or a forgery. Refuse rather than guess.
            return Error.BusinessRule("gate.tap_reused", "This tapId was already used for a different tap.");
        }

        return new GateOutcome(JsonSerializer.Deserialize<GateDecision>(seen.Response, JsonSerializerOptions.Web)!, Replayed: true);
    }

    private static readonly Error TicketNotFound = Error.NotFound("ticket.not_found", "No ticket with this id.");

    private static Task<GateTap?> FindAsync(CoreDbContext db, Guid tapId, CancellationToken ct) =>
        db.GateTaps.AsNoTracking().SingleOrDefaultAsync(t => t.TapId == tapId, ct);

    /// <summary>By primary key, and only if the QR's subject owns it (the signature already proved the pair).</summary>
    private static Task<Ticket?> ReadTicketAsync(CoreDbContext db, TicketCredentialClaims claims, CancellationToken ct) =>
        db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == claims.TicketId && t.UserId == claims.UserId, ct);

    private static GateDecision Decision(GateTapCommand tap, int status, string code, string message, Ticket ticket) =>
        Decision(tap, status, code, message, ticket.Id, ticket.Status, 0);

    private static GateDecision Decision(
        GateTapCommand tap, int status, string code, string message, Guid ticketId, TicketStatus ticketStatus, long surcharge) =>
        new(tap.TapId, status == StatusCodes.Status200OK, status, code, message,
            tap.Direction == GateDirection.Entry ? "entry" : "exit", ticketId, tap.Gate.Station, ticketStatus.ToString(), surcharge);

    internal static string Hash(GateTapCommand tap) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{tap.Gate.Id}\n{tap.Gate.Station}\n{tap.Direction}\n{tap.Credential}")));
}
