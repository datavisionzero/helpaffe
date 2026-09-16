using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectSupportInstructions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "support_instructions",
                table: "projects",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "support_instructions",
                table: "projects");
        }
    }
}
