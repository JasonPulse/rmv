using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterPortraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dropped and re-added rather than renamed, and that is not incidental.
            //
            // The old columns held URLs a browser fetched directly. The new one
            // asserts that we hold the bytes, and we hold none yet. Carrying the
            // old values across would break twice: every existing character would
            // claim a picture the portrait endpoint cannot serve, rendering a broken
            // image, and the next refresh would compare the Lodestone's own URL
            // against itself, decide nothing had changed, and never download it.
            //
            // Starting empty, the first refresh fetches and stores properly. Do not
            // turn this into a RenameColumn.
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "PortraitUrl",
                table: "characters");

            migrationBuilder.AddColumn<string>(
                name: "PortraitVersion",
                table: "characters",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "character_portraits",
                columns: table => new
                {
                    CharacterId = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_character_portraits", x => x.CharacterId);
                    table.ForeignKey(
                        name: "FK_character_portraits_characters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "characters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "character_portraits");

            migrationBuilder.DropColumn(
                name: "PortraitVersion",
                table: "characters");

            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "characters",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PortraitUrl",
                table: "characters",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);
        }
    }
}
