using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemberApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "members",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "members",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // "Pending", not "". EF's default for a string-converted enum is
                // an empty string, which does not map to any MemberStatus, so
                // every member row that existed before this migration would throw
                // on read. Anyone who signed in before approval existed is
                // treated as pending, which is the safe direction: root admins
                // come from configuration and keep their access regardless.
                defaultValue: "Pending");

            migrationBuilder.CreateIndex(
                name: "IX_members_Status",
                table: "members",
                column: "Status");

            migrationBuilder.Sql(
                """UPDATE members SET "Status" = 'Pending' WHERE "Status" IS NULL OR "Status" = '';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_members_Status",
                table: "members");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "members");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "members");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "members");
        }
    }
}
