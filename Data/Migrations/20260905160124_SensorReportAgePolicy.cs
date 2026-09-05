using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class SensorReportAgePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaximumReportAgeMinutes",
                table: "ThermalRoomConfigs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaximumReportAgeMinutes",
                table: "ThermalEntityConfigs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaximumReportAgeMinutes",
                table: "ThermalRoomConfigs");

            migrationBuilder.DropColumn(
                name: "MaximumReportAgeMinutes",
                table: "ThermalEntityConfigs");
        }
    }
}
