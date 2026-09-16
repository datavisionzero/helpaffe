using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketConcurrencyAndIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "tickets",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WaitingSince",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql("UPDATE tickets SET \"WaitingSince\" = \"CreatedAt\";");

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    CredentialKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseStatus = table.Column<int>(type: "integer", nullable: false),
                    ResponseBody = table.Column<string>(type: "text", nullable: false),
                    ResponseETag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => new { x.CredentialKind, x.CredentialId, x.Key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_Status_Priority_WaitingSince_Id",
                table: "tickets",
                columns: new[] { "Status", "Priority", "WaitingSince", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_tickets_Version_Positive",
                table: "tickets",
                sql: "\"Version\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_ExpiresAt",
                table: "idempotency_records",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropIndex(
                name: "IX_tickets_Status_Priority_WaitingSince_Id",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_tickets_Version_Positive",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "WaitingSince",
                table: "tickets");
        }
    }
}
