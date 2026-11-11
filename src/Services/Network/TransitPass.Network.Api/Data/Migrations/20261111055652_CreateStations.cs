using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TransitPass.Network.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateStations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stations",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stations", x => x.id);
                    table.CheckConstraint("ck_stations_id_slug", "id ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                });

            migrationBuilder.InsertData(
                table: "stations",
                columns: new[] { "id", "name" },
                values: new object[,]
                {
                    { "agargaon", "Agargaon" },
                    { "karwan-bazar", "Karwan Bazar" },
                    { "motijheel", "Motijheel" },
                    { "uttara-north", "Uttara North" }
                });

            migrationBuilder.CreateIndex(
                name: "ux_stations_name",
                table: "stations",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stations");
        }
    }
}
