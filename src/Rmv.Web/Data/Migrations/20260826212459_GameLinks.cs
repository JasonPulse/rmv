using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GamePresenceId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Url = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_game_links_game_presences_GamePresenceId",
                        column: x => x.GamePresenceId,
                        principalTable: "game_presences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "game_links",
                columns: new[] { "Id", "GamePresenceId", "Kind", "Label", "SortOrder", "Url" },
                values: new object[] { 1, 2, "Herald", "Uthgard Herald", 0, "https://herald.uthgard.net/herald.php?view=overview" });

            migrationBuilder.CreateIndex(
                name: "IX_game_links_GamePresenceId_SortOrder",
                table: "game_links",
                columns: new[] { "GamePresenceId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_links");
        }
    }
}
