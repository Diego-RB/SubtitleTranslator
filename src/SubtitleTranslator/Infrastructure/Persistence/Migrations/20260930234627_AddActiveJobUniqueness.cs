using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveJobUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_SourcePath",
                table: "Jobs");

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "Chunks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrchestratorSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DefaultPipelineId = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultPromptVersionId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrchestratorSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrchestratorSettings_Pipelines_DefaultPipelineId",
                        column: x => x.DefaultPipelineId,
                        principalTable: "Pipelines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrchestratorSettings_PromptVersions_DefaultPromptVersionId",
                        column: x => x.DefaultPromptVersionId,
                        principalTable: "PromptVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SourcePath",
                table: "Jobs",
                column: "SourcePath",
                unique: true,
                filter: "\"Status\" NOT IN ('Completed', 'Failed', 'Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_OrchestratorSettings_DefaultPipelineId",
                table: "OrchestratorSettings",
                column: "DefaultPipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_OrchestratorSettings_DefaultPromptVersionId",
                table: "OrchestratorSettings",
                column: "DefaultPromptVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrchestratorSettings");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_SourcePath",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "Chunks");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SourcePath",
                table: "Jobs",
                column: "SourcePath");
        }
    }
}
