using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptVersionName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "PromptVersions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "PromptVersions");
        }
    }
}
