using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineStepOrderingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PipelineSteps_PipelineId",
                table: "PipelineSteps");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSteps_PipelineId_Order",
                table: "PipelineSteps",
                columns: new[] { "PipelineId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PipelineSteps_PipelineId_Order",
                table: "PipelineSteps");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineSteps_PipelineId",
                table: "PipelineSteps",
                column: "PipelineId");
        }
    }
}
