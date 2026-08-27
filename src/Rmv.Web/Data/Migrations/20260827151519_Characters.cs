using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Characters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeraldAdapterKey",
                table: "game_presences",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeraldBaseUrl",
                table: "game_presences",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    GamePresenceId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Guild = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Realm = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Class = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Race = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: true),
                    RealmRank = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Score = table.Column<long>(type: "bigint", nullable: true),
                    Kills = table.Column<long>(type: "bigint", nullable: true),
                    Deaths = table.Column<long>(type: "bigint", nullable: true),
                    LastOnline = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    HeraldUrl = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastFetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_characters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_characters_game_presences_GamePresenceId",
                        column: x => x.GamePresenceId,
                        principalTable: "game_presences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_characters_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "game_presences",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "HeraldAdapterKey", "HeraldBaseUrl" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "game_presences",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "HeraldAdapterKey", "HeraldBaseUrl" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "game_presences",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "HeraldAdapterKey", "HeraldBaseUrl" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                table: "game_presences",
                keyColumn: "Id",
                keyValue: 4,
                columns: new[] { "HeraldAdapterKey", "HeraldBaseUrl" },
                values: new object[] { null, null });

            migrationBuilder.CreateIndex(
                name: "IX_characters_GamePresenceId_Name",
                table: "characters",
                columns: new[] { "GamePresenceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_characters_MemberId",
                table: "characters",
                column: "MemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "characters");

            migrationBuilder.DropColumn(
                name: "HeraldAdapterKey",
                table: "game_presences");

            migrationBuilder.DropColumn(
                name: "HeraldBaseUrl",
                table: "game_presences");
        }
    }
}
