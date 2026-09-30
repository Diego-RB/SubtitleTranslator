using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAzureBatchLifecycleToTranslationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchCompletedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AzureBatchCompletedRequests",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchCreatedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBatchErrorFileId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchExpiresAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AzureBatchFailedRequests",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchInProgressAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBatchInputFileId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchLastPolledAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AzureBatchLastProgressAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBatchOutputFileId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureBatchStatus",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AzureBatchTotalRequests",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AzureBatchCompletedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchCompletedRequests",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchCreatedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchErrorFileId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchExpiresAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchFailedRequests",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchInProgressAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchInputFileId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchLastPolledAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchLastProgressAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchOutputFileId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchStatus",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AzureBatchTotalRequests",
                table: "Jobs");
        }
    }
}
