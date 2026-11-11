using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TransitPass.Network.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreateSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "segments",
                columns: table => new
                {
                    station_a = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    station_b = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    distance_m = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_segments", x => new { x.station_a, x.station_b });
                    table.CheckConstraint("ck_segments_canonical_order", "station_a COLLATE \"C\" < station_b COLLATE \"C\"");
                    table.CheckConstraint("ck_segments_distance_positive", "distance_m > 0");
                    table.ForeignKey(
                        name: "fk_segments_station_a",
                        column: x => x.station_a,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_segments_station_b",
                        column: x => x.station_b,
                        principalTable: "stations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "segments",
                columns: new[] { "station_a", "station_b", "distance_m" },
                values: new object[,]
                {
                    { "agargaon", "karwan-bazar", 4000 },
                    { "agargaon", "uttara-north", 9000 },
                    { "karwan-bazar", "motijheel", 3500 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_segments_station_b",
                table: "segments",
                column: "station_b");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "segments");
        }
    }
}
