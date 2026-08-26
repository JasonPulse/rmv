using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class HistoryAndAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_presences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Game = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Guilds = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    Period = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_presences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "request_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Referrer = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    IsBot = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_logs", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "game_presences",
                columns: new[] { "Id", "Game", "Guilds", "IsActive", "Period", "SortOrder" },
                values: new object[,]
                {
                    { 1, "Blackthorn DAoC", "Dark Auspices", true, null, 0 },
                    { 2, "Uthgard DAoC", "RMV, Legends, Dark Auspices", false, null, 0 },
                    { 3, "World of Warcraft", "RMV, Omen, Etc.", false, null, 1 },
                    { 4, "Final Fantasy XI", "RMV", false, null, 2 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_game_presences_IsActive_SortOrder",
                table: "game_presences",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_At",
                table: "request_logs",
                column: "At",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_Path_At",
                table: "request_logs",
                columns: new[] { "Path", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_Status_At",
                table: "request_logs",
                columns: new[] { "Status", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_presences");

            migrationBuilder.DropTable(
                name: "request_logs");
        }
    }
}
