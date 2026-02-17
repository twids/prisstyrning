using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Prisstyrning.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    HasHangfireAccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRoles", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "DaikinTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccessToken = table.Column<string>(type: "text", nullable: false),
                    RefreshToken = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DaikinTokens", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "PriceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Zone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    SavedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TodayPricesJson = table.Column<string>(type: "jsonb", nullable: false),
                    TomorrowPricesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SchedulePayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSettings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ComfortHours = table.Column<int>(type: "integer", nullable: false),
                    TurnOffPercentile = table.Column<double>(type: "double precision", nullable: false),
                    MaxComfortGapHours = table.Column<int>(type: "integer", nullable: false),
                    AutoApplySchedule = table.Column<bool>(type: "boolean", nullable: false),
                    Zone = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "SE3")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSettings", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceSnapshots_Zone_Date",
                table: "PriceSnapshots",
                columns: new[] { "Zone", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleHistory_UserId",
                table: "ScheduleHistory",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminRoles");

            migrationBuilder.DropTable(
                name: "DaikinTokens");

            migrationBuilder.DropTable(
                name: "PriceSnapshots");

            migrationBuilder.DropTable(
                name: "ScheduleHistory");

            migrationBuilder.DropTable(
                name: "UserSettings");
        }
    }
}
