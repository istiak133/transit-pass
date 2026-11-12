using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransitPass.Core.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateTopupsAndPaymentInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_events",
                columns: table => new
                {
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    topup_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_events", x => new { x.provider, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "topups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_poisha = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_topups", x => x.id);
                    table.CheckConstraint("ck_topups_amount_positive", "amount_poisha > 0");
                    table.CheckConstraint("ck_topups_completed_at", "(status = 'Pending') = (completed_at IS NULL)");
                    table.CheckConstraint("ck_topups_status", "status IN ('Pending', 'Succeeded', 'Failed')");
                    table.ForeignKey(
                        name: "fk_topups_wallet",
                        column: x => x.wallet_id,
                        principalTable: "wallets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_ledger_entries_topup_ref",
                table: "ledger_entries",
                column: "ref_id",
                unique: true,
                filter: "type = 'TopUp'");

            migrationBuilder.CreateIndex(
                name: "ix_topups_wallet_id_id",
                table: "topups",
                columns: new[] { "wallet_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_events");

            migrationBuilder.DropTable(
                name: "topups");

            migrationBuilder.DropIndex(
                name: "ux_ledger_entries_topup_ref",
                table: "ledger_entries");
        }
    }
}
