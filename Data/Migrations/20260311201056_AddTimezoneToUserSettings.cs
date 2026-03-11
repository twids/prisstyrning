using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimezoneToUserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "UserSettings",
                type: "text",
                nullable: false,
                defaultValue: "auto");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "UserSettings");
        }
    }
}
