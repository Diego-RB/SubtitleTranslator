using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptVersionToTranslationJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PromptVersionId",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_PromptVersionId",
                table: "Jobs",
                column: "PromptVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_PromptVersions_PromptVersionId",
                table: "Jobs",
                column: "PromptVersionId",
                principalTable: "PromptVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_PromptVersions_PromptVersionId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_PromptVersionId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PromptVersionId",
                table: "Jobs");
        }
    }
}
