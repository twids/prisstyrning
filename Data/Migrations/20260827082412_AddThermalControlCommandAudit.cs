using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddThermalControlCommandAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SpotPriceSekPerKwh",
                table: "ThermalTelemetrySamples",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ThermalControlCommands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommandType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Target = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RequestedValue = table.Column<double>(type: "double precision", nullable: true),
                    PreviousValue = table.Column<double>(type: "double precision", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalControlCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalControlCommands_UserId_TimestampUtc",
                table: "ThermalControlCommands",
                columns: new[] { "UserId", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThermalControlCommands");

            migrationBuilder.DropColumn(
                name: "SpotPriceSekPerKwh",
                table: "ThermalTelemetrySamples");
        }
    }
}
