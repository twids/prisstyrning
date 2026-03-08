using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Prisstyrning.Data.Migrations
{
    /// <inheritdoc />
    public partial class FlexibleSchedulingDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "EcoIntervalHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 24,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "EcoFlexibilityHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 12,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "ComfortIntervalDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 21,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "ComfortFlexibilityDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<double>(
                name: "ComfortEarlyPercentile",
                table: "UserSettings",
                type: "double precision",
                nullable: false,
                defaultValueSql: "0.1",
                oldClrType: typeof(double),
                oldType: "double precision");

            // Backfill existing rows that have zero/unset values
            migrationBuilder.Sql(
                """
                UPDATE "UserSettings" SET "EcoIntervalHours" = 24 WHERE "EcoIntervalHours" = 0;
                UPDATE "UserSettings" SET "EcoFlexibilityHours" = 12 WHERE "EcoFlexibilityHours" = 0;
                UPDATE "UserSettings" SET "ComfortIntervalDays" = 21 WHERE "ComfortIntervalDays" = 0;
                UPDATE "UserSettings" SET "ComfortFlexibilityDays" = 7 WHERE "ComfortFlexibilityDays" = 0;
                UPDATE "UserSettings" SET "ComfortEarlyPercentile" = 0.10 WHERE "ComfortEarlyPercentile" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "EcoIntervalHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 24);

            migrationBuilder.AlterColumn<int>(
                name: "EcoFlexibilityHours",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 12);

            migrationBuilder.AlterColumn<int>(
                name: "ComfortIntervalDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 21);

            migrationBuilder.AlterColumn<int>(
                name: "ComfortFlexibilityDays",
                table: "UserSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 7);

            migrationBuilder.AlterColumn<double>(
                name: "ComfortEarlyPercentile",
                table: "UserSettings",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldDefaultValueSql: "0.1");
        }
    }
}
