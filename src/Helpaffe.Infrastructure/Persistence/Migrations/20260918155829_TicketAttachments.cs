using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    media_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_attachments", x => x.Id);
                    table.CheckConstraint("CK_ticket_attachments_Size", "\"size\" > 0 AND \"size\" <= 10485760");
                    table.CheckConstraint("CK_ticket_attachments_StorageKey", "length(\"storage_key\") = 64");
                    table.ForeignKey(
                        name: "FK_ticket_attachments_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ticket_attachments_ticket_conversation_conversation_entry_id",
                        column: x => x.conversation_entry_id,
                        principalTable: "ticket_conversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_attachments_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attachments_conversation_entry_id_created_at_Id",
                table: "ticket_attachments",
                columns: new[] { "conversation_entry_id", "created_at", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attachments_project_id",
                table: "ticket_attachments",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attachments_storage_key",
                table: "ticket_attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ticket_attachments_ticket_id",
                table: "ticket_attachments",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_attachments");
        }
    }
}
