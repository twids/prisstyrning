using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFlexibleScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ComfortEarlyPercentile",
                table: "UserSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "ComfortFlexibilityDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ComfortIntervalDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EcoFlexibilityHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EcoIntervalHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SchedulingMode",
                table: "UserSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Classic");

            migrationBuilder.CreateTable(
                name: "FlexibleScheduleStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastEcoRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastComfortRunUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlexibleScheduleStates", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlexibleScheduleStates");

            migrationBuilder.DropColumn(
                name: "ComfortEarlyPercentile",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ComfortFlexibilityDays",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "ComfortIntervalDays",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "EcoFlexibilityHours",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "EcoIntervalHours",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "SchedulingMode",
                table: "UserSettings");
        }
    }
}
