using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TransitPass.Core.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateTicketsAndIdempotencyKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.user_id, x.key });
                    table.CheckConstraint("ck_idempotency_keys_response_complete", "(ticket_id IS NULL) = (response_body IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_station = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_station = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fare_poisha = table.Column<long>(type: "bigint", nullable: false),
                    distance_m = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    purchased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_different_stations", "to_station <> from_station");
                    table.CheckConstraint("ck_tickets_expiry_after_purchase", "expires_at > purchased_at");
                    table.CheckConstraint("ck_tickets_fare_positive", "fare_poisha > 0");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Active', 'InJourney', 'Used', 'Cancelled', 'Expired')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_active_expiry",
                table: "tickets",
                column: "expires_at",
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_user_id_id",
                table: "tickets",
                columns: new[] { "user_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "tickets");
        }
    }
}
