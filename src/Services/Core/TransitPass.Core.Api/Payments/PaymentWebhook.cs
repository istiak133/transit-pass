using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TransitPass.BuildingBlocks.Http;
using TransitPass.BuildingBlocks.Results;
using TransitPass.Core.Api.Data;
using TransitPass.Core.Api.Ledger;
using TransitPass.Core.Api.Topups;

namespace TransitPass.Core.Api.Payments;

/// <summary>The provider's notification body (what we read after the signature checks out).</summary>
public sealed record PaymentEventBody(string? EventId, string? Type, Guid? TopupId, long? AmountPoisha);

/// <summary>What we answer the provider: applied, duplicate (same event again) or already_final (top-up was done).</summary>
public sealed record WebhookAck(string Result);

/// <summary>POST /api/v1/payments/webhook: the only way money enters a wallet.</summary>
public static class PaymentWebhook
{
    internal const string Succeeded = "payment.succeeded";
    internal const string Failed = "payment.failed";
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>Maps the webhook route. It is anonymous: the caller proves itself with the HMAC, not a user token.</summary>
    public static IEndpointRouteBuilder MapPaymentWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/payments/webhook", async (
            HttpRequest request, CoreDbContext db, WebhookSecret secret, IOptions<PaymentOptions> options,
            TimeProvider clock, CancellationToken ct) =>
        {
            // 1. The raw bytes, exactly as sent (capped, so a huge body can't exhaust memory).
            byte[]? body = await ReadCappedAsync(request.Body, MaxBodyBytes, ct);
            if (body is null)
            {
                return Error.Validation("payment.body_too_large", "The notification body is too large.").ToProblem();
            }

            // 2. Authenticate before parsing: an unsigned body is never even deserialized.
            DateTimeOffset now = clock.GetUtcNow();
            if (!WebhookSignature.Verify(request.Headers[WebhookSignature.Header], body, secret.Bytes, now,
                    options.Value.SignatureTolerance))
            {
                return Error.Unauthorized("payment.invalid_signature", "The notification signature is not valid.").ToProblem();
            }

            // 3. Parse and validate.
            var parsed = Parse(body);
            if (!parsed.IsSuccess)
            {
                return parsed.Error.ToProblem();
            }

            // 4. Apply, in one transaction.
            var applied = await ApplyAsync(db, options.Value.Provider, parsed.Value, now, ct);
            return applied.IsSuccess ? TypedResults.Ok(new WebhookAck(applied.Value)) : applied.Error.ToProblem();
        });

        return app;
    }

    internal static Result<PaymentEventBody> Parse(byte[] body)
    {
        PaymentEventBody? evt;
        try
        {
            evt = JsonSerializer.Deserialize<PaymentEventBody>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            evt = null;
        }

        if (evt is not { EventId.Length: > 0 and <= 100, TopupId: not null, AmountPoisha: > 0 }
            || evt.Type is not (Succeeded or Failed))
        {
            return Error.Validation("payment.invalid_event", "The notification is malformed.");
        }

        return evt;
    }

    /// <summary>
    /// Three guards, so a top-up is credited at most once whatever arrives:
    /// (1) the inbox PK drops a re-delivered event; (2) the conditional status UPDATE drops a second,
    /// different event for a finished top-up; (3) a partial UNIQUE index allows one TopUp ledger row per top-up.
    /// Any error rolls everything back, including the inbox row, so the provider's retry is processed fresh.
    /// </summary>
    internal static async Task<Result<string>> ApplyAsync(
        CoreDbContext db, string provider, PaymentEventBody evt, DateTimeOffset now, CancellationToken ct)
    {
        Guid topupId = evt.TopupId!.Value;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Guard 1: the inbox. ON CONFLICT DO NOTHING instead of catching 23505, because an error inside a
        // Postgres transaction aborts it. If another request holds the same key uncommitted, this waits for it.
        int recorded = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO payment_events (provider, event_id, type, topup_id, received_at)
            VALUES ({provider}, {evt.EventId}, {evt.Type}, {topupId}, {now})
            ON CONFLICT (provider, event_id) DO NOTHING
            """, ct);
        if (recorded == 0)
        {
            return "duplicate";
        }

        Topup? topup = await db.Topups.AsNoTracking().SingleOrDefaultAsync(t => t.Id == topupId, ct);
        if (topup is null)
        {
            return Error.NotFound("payment.unknown_topup", "No top-up has this id.");
        }

        if (topup.AmountPoisha != evt.AmountPoisha)
        {
            return Error.BusinessRule("payment.amount_mismatch", "The paid amount does not match the top-up.");
        }

        // Guard 2: the state machine, as one atomic statement. Only a Pending row can move. Two racing
        // events both try; the row lock makes the second wait, re-check status, and update 0 rows.
        TopupStatus next = evt.Type == Succeeded ? TopupStatus.Succeeded : TopupStatus.Failed;
        int moved = await db.Topups
            .Where(t => t.Id == topupId && t.Status == TopupStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, next).SetProperty(t => t.CompletedAt, now), ct);
        if (moved == 0)
        {
            await tx.CommitAsync(ct); // keep the inbox row: this event is handled (nothing to do)
            return "already_final";
        }

        if (next == TopupStatus.Succeeded)
        {
            // Phase 7's LedgerBook joins this transaction: status change + balance + ledger row commit together.
            var credited = await LedgerBook.PostAsync(db, topup.WalletId, topup.AmountPoisha, LedgerEntryType.TopUp, topup.Id, now, ct);
            if (!credited.IsSuccess)
            {
                return credited.Error;
            }
        }

        await tx.CommitAsync(ct);
        return "applied";
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
