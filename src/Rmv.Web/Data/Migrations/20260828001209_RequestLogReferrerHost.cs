using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rmv.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestLogReferrerHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReferrerHost",
                table: "request_logs",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            // Backfilled, not left for new traffic only. The whole reason this column
            // exists is a set of requests already in the table: hits on a signature
            // generator that has been gone for ten years. Answering "where are they
            // coming from" only for future requests would answer the wrong question.
            //
            // Regex rather than a call into the app's own parser, because this runs
            // once inside the migration. It has to agree with
            // RequestLogMiddleware.HostOf on the shape a browser actually sends:
            // scheme, host, then a delimiter or the end of the string. The trailing
            // delimiter is what makes it agree on a long host: without it this would
            // store the first 253 characters of a 300 character host while HostOf
            // rejects it outright. Anything unmatched is left null, which is the
            // same answer HostOf gives. ReferrerHostTests pins the agreement.
            migrationBuilder.Sql("""
                UPDATE request_logs
                SET "ReferrerHost" = lower(
                        substring("Referrer" from '^https?://([^/?#]{1,253})(?:[/?#]|$)'))
                WHERE "Referrer" IS NOT NULL
                  AND "Referrer" ~* '^https?://[^/?#]{1,253}([/?#]|$)';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_request_logs_ReferrerHost",
                table: "request_logs",
                column: "ReferrerHost");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_request_logs_ReferrerHost",
                table: "request_logs");

            migrationBuilder.DropColumn(
                name: "ReferrerHost",
                table: "request_logs");
        }
    }
}
