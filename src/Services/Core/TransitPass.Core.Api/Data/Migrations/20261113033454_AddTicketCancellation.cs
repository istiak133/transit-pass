using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransitPass.Core.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_cancelled_at",
                table: "tickets",
                sql: "(status = 'Cancelled') = (cancelled_at IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ux_ledger_entries_refund_ref",
                table: "ledger_entries",
                column: "ref_id",
                unique: true,
                filter: "type = 'Refund'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_cancelled_at",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ux_ledger_entries_refund_ref",
                table: "ledger_entries");

            migrationBuilder.DropColumn(
                name: "cancelled_at",
                table: "tickets");
        }
    }
}
