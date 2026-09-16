using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX "IX_tickets_FullText"
                ON tickets USING GIN (
                    to_tsvector('simple', "Number" || ' ' || "Subject" || ' ' || requester_name || ' ' || requester_email)
                );
                """);
            migrationBuilder.Sql("""
                CREATE INDEX "IX_ticket_conversation_FullText"
                ON ticket_conversation USING GIN (to_tsvector('simple', "Body"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_ticket_conversation_FullText\";");
            migrationBuilder.Sql("DROP INDEX \"IX_tickets_FullText\";");
        }
    }
}
