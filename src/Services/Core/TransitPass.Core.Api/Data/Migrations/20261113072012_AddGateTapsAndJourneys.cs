using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransitPass.Core.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGateTapsAndJourneys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gate_taps",
                columns: table => new
                {
                    tap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gate_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    station = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    allowed = table.Column<bool>(type: "boolean", nullable: false),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gate_taps", x => x.tap_id);
                    table.CheckConstraint("ck_gate_taps_allowed_result", "allowed = (result IN ('gate.entered', 'gate.exited'))");
                    table.CheckConstraint("ck_gate_taps_direction", "direction IN ('Entry', 'Exit')");
                    table.ForeignKey(
                        name: "fk_gate_taps_ticket",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "journeys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_station = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entry_tap_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    exit_station = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    exit_tap_id = table.Column<Guid>(type: "uuid", nullable: true),
                    exited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    surcharge_poisha = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_journeys", x => x.id);
                    table.CheckConstraint("ck_journeys_completed_has_exit", "(status = 'Completed') = (exited_at IS NOT NULL)");
                    table.CheckConstraint("ck_journeys_exit_after_entry", "exited_at >= entered_at");
                    table.CheckConstraint("ck_journeys_exit_all_or_nothing", "num_nonnulls(exit_station, exit_tap_id, exited_at, surcharge_poisha) IN (0, 4)");
                    table.CheckConstraint("ck_journeys_status", "status IN ('InProgress', 'Completed')");
                    table.CheckConstraint("ck_journeys_surcharge_not_negative", "surcharge_poisha >= 0");
                    table.ForeignKey(
                        name: "fk_journeys_ticket",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_entries_surcharge_ref",
                table: "ledger_entries",
                column: "ref_id",
                unique: true,
                filter: "type = 'Surcharge'");

            migrationBuilder.CreateIndex(
                name: "ix_gate_taps_ticket_id_created_at",
                table: "gate_taps",
                columns: new[] { "ticket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_journeys_ticket_id",
                table: "journeys",
                column: "ticket_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gate_taps");

            migrationBuilder.DropTable(
                name: "journeys");

            migrationBuilder.DropIndex(
                name: "ux_ledger_entries_surcharge_ref",
                table: "ledger_entries");
        }
    }
}
