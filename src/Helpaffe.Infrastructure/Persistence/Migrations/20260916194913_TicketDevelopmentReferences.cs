using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketDevelopmentReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_development_references",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_development_references", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ticket_development_references_tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_development_references_TicketId_Position",
                table: "ticket_development_references",
                columns: new[] { "TicketId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_development_references_TicketId_url",
                table: "ticket_development_references",
                columns: new[] { "TicketId", "url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_development_references");
        }
    }
}
