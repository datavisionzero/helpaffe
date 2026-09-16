using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpaffe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SolutionArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "solution_articles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    markdown = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_solution_articles", x => x.Id);
                    table.CheckConstraint("CK_solution_articles_Version_Positive", "\"version\" >= 1");
                    table.ForeignKey(
                        name: "FK_solution_articles_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_solution_articles_ProjectId_key",
                table: "solution_articles",
                columns: new[] { "ProjectId", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_solution_articles_ProjectId_updated_at_Id",
                table: "solution_articles",
                columns: new[] { "ProjectId", "updated_at", "Id" });

            migrationBuilder.Sql("""
                CREATE INDEX "IX_solution_articles_FullText"
                ON solution_articles USING GIN (to_tsvector('simple', title || ' ' || markdown));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_solution_articles_FullText\";");

            migrationBuilder.DropTable(
                name: "solution_articles");
        }
    }
}
