using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddThermalStartupState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThermalStartupStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Phase = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ConservativeEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CommissionedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastEvaluationUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalStartupStates", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThermalStartupStates");
        }
    }
}
