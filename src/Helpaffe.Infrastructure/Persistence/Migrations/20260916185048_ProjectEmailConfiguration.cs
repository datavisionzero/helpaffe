using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectEmailConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_email_settings",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    smtp_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    smtp_port = table.Column<int>(type: "integer", nullable: false),
                    smtp_use_tls = table.Column<bool>(type: "boolean", nullable: false),
                    smtp_username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    smtp_password_ciphertext = table.Column<string>(type: "text", nullable: true),
                    sender_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sender_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    support_recipients_json = table.Column<string>(type: "text", nullable: false),
                    brand_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    brand_logo_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    brand_color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    customer_ticket_url_template = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    backoffice_ticket_url_template = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_email_settings", x => x.ProjectId);
                    table.ForeignKey(
                        name: "FK_project_email_settings_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_email_templates",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_email_templates", x => new { x.ProjectId, x.Type });
                    table.ForeignKey(
                        name: "FK_project_email_templates_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_email_settings");

            migrationBuilder.DropTable(
                name: "project_email_templates");
        }
    }
}
