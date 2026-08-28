using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddWeatherForecastTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OutsideTemperatureForecastJson",
                table: "ThermalTelemetrySamples",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<double>(
                name: "SolarIrradianceWm2",
                table: "ThermalTelemetrySamples",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "WindSpeedMps",
                table: "ThermalTelemetrySamples",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutsideTemperatureForecastJson",
                table: "ThermalTelemetrySamples");

            migrationBuilder.DropColumn(
                name: "SolarIrradianceWm2",
                table: "ThermalTelemetrySamples");

            migrationBuilder.DropColumn(
                name: "WindSpeedMps",
                table: "ThermalTelemetrySamples");
        }
    }
}
