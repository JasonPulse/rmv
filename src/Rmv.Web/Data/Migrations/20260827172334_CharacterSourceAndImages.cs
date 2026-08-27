using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class CharacterSourceAndImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            // "Herald", not the scaffolded "". The column maps to an enum, and an
            // empty string is not one of its names, so every character that
            // already exists would throw on read. Every one of them was fetched
            // from a herald, which is what makes Herald the truthful backfill
            // rather than merely a value that parses.
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "characters",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Herald");

            migrationBuilder.Sql(
                "UPDATE characters SET \"Source\" = 'Herald' WHERE \"Source\" = '' OR \"Source\" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "PortraitUrl",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "characters");
        }
    }
}
