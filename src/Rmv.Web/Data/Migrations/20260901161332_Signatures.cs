using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Signatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signature_backgrounds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signature_backgrounds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_signature_backgrounds_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    Slug = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Design = table.Column<string>(type: "jsonb", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_signatures_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signature_images",
                columns: table => new
                {
                    SignatureId = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RenderedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signature_images", x => x.SignatureId);
                    table.ForeignKey(
                        name: "FK_signature_images_signatures_SignatureId",
                        column: x => x.SignatureId,
                        principalTable: "signatures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_signature_backgrounds_MemberId_UploadedAt",
                table: "signature_backgrounds",
                columns: new[] { "MemberId", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_signatures_MemberId",
                table: "signatures",
                column: "MemberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signatures_Slug",
                table: "signatures",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signature_backgrounds");

            migrationBuilder.DropTable(
                name: "signature_images");

            migrationBuilder.DropTable(
                name: "signatures");
        }
    }
}
