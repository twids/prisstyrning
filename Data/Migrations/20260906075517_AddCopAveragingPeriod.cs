using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddCopAveragingPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AveragingPeriod",
                table: "ThermalEntityConfigs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AveragingPeriod",
                table: "ThermalEntityConfigs");
        }
    }
}
