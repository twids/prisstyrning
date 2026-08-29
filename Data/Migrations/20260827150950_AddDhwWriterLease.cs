using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddDhwWriterLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DhwLeaseExpiresUtc",
                table: "ThermalSiteConfigs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DhwLeaseOwner",
                table: "ThermalSiteConfigs",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DhwLeaseExpiresUtc",
                table: "ThermalSiteConfigs");

            migrationBuilder.DropColumn(
                name: "DhwLeaseOwner",
                table: "ThermalSiteConfigs");
        }
    }
}
