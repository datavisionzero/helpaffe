using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentAndProductCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    AllProjects = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_credentials_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_prefix = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_api_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_product_api_keys_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_project_access",
                columns: table => new
                {
                    AgentCredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_project_access", x => new { x.AgentCredentialId, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_agent_project_access_agent_credentials_AgentCredentialId",
                        column: x => x.AgentCredentialId,
                        principalTable: "agent_credentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_project_access_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_UserId",
                table: "agent_credentials",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_credentials_token_hash",
                table: "agent_credentials",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_project_access_ProjectId",
                table: "agent_project_access",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_product_api_keys_ProjectId",
                table: "product_api_keys",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_product_api_keys_token_hash",
                table: "product_api_keys",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_project_access");

            migrationBuilder.DropTable(
                name: "product_api_keys");

            migrationBuilder.DropTable(
                name: "agent_credentials");
        }
    }
}
