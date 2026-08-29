using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Screenshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "screenshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    GamePresenceId = table.Column<int>(type: "integer", nullable: true),
                    Caption = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<int>(type: "integer", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screenshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_screenshots_game_presences_GamePresenceId",
                        column: x => x.GamePresenceId,
                        principalTable: "game_presences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_screenshots_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "screenshot_images",
                columns: table => new
                {
                    ScreenshotId = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screenshot_images", x => x.ScreenshotId);
                    table.ForeignKey(
                        name: "FK_screenshot_images_screenshots_ScreenshotId",
                        column: x => x.ScreenshotId,
                        principalTable: "screenshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_screenshots_GamePresenceId",
                table: "screenshots",
                column: "GamePresenceId");

            migrationBuilder.CreateIndex(
                name: "IX_screenshots_MemberId_UploadedAt",
                table: "screenshots",
                columns: new[] { "MemberId", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_screenshots_UploadedAt",
                table: "screenshots",
                column: "UploadedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "screenshot_images");

            migrationBuilder.DropTable(
                name: "screenshots");
        }
    }
}
