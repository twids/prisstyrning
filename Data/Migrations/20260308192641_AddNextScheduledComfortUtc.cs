using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNextScheduledComfortUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextScheduledComfortUtc",
                table: "FlexibleScheduleStates",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextScheduledComfortUtc",
                table: "FlexibleScheduleStates");
        }
    }
}
