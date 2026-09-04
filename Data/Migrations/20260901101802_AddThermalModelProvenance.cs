using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddThermalModelProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceEvidenceJson",
                table: "ThermalModelVersions",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceEvidenceJson",
                table: "ThermalModelVersions");
        }
    }
}
