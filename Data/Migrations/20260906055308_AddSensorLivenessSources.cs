using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorLivenessSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FreshnessAttribute",
                table: "ThermalRoomConfigs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreshnessEntityId",
                table: "ThermalRoomConfigs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreshnessAttribute",
                table: "ThermalEntityConfigs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FreshnessEntityId",
                table: "ThermalEntityConfigs",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FreshnessAttribute",
                table: "ThermalRoomConfigs");

            migrationBuilder.DropColumn(
                name: "FreshnessEntityId",
                table: "ThermalRoomConfigs");

            migrationBuilder.DropColumn(
                name: "FreshnessAttribute",
                table: "ThermalEntityConfigs");

            migrationBuilder.DropColumn(
                name: "FreshnessEntityId",
                table: "ThermalEntityConfigs");
        }
    }
}
