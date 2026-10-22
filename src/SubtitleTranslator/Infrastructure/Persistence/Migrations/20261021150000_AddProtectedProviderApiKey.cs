using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SubtitleTranslator.Infrastructure.Persistence;

#nullable disable

namespace SubtitleTranslator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SubtitleTranslatorDbContext))]
[Migration("20261021150000_AddProtectedProviderApiKey")]
public partial class AddProtectedProviderApiKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProtectedApiKey",
            table: "Providers",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProtectedApiKey",
            table: "Providers");
    }
}