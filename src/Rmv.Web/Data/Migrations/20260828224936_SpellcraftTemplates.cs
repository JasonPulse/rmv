using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SpellcraftTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A new table, so there is nothing to backfill and no enum stored as
            // text to give a real default to.
            //
            // The unique index on (MemberId, Ordinal) and the check constraint on
            // Ordinal are the five-template cap. They are here rather than only in
            // the store because a count in a handler loses a race and an index does
            // not. If the cap ever changes, SpellcraftTemplate.MaxPerMember moves
            // and a new migration rewrites the constraint; do not edit this one.
            migrationBuilder.CreateTable(
                name: "spellcraft_templates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MemberId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Design = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spellcraft_templates", x => x.Id);
                    table.CheckConstraint("ck_spellcraft_templates_ordinal", "\"Ordinal\" >= 1 AND \"Ordinal\" <= 5");
                    table.ForeignKey(
                        name: "FK_spellcraft_templates_members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_spellcraft_templates_MemberId_Ordinal",
                table: "spellcraft_templates",
                columns: new[] { "MemberId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spellcraft_templates");
        }
    }
}
