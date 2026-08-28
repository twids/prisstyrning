using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Prisstyrning.data.Migrations
{
    /// <inheritdoc />
    public partial class AddIntelligentThermalOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DhwCycles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlannedStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScheduleAcceptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActualStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TargetReachedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActualEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    TargetTemperatureC = table.Column<double>(type: "double precision", nullable: false),
                    PredictedDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    ReservedDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    PredictedCost = table.Column<decimal>(type: "numeric", nullable: true),
                    ActualCost = table.Column<decimal>(type: "numeric", nullable: true),
                    BackupHeaterUsed = table.Column<bool>(type: "boolean", nullable: false),
                    PowerProfileJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetVerificationCount = table.Column<int>(type: "integer", nullable: false),
                    EstimatedCompletionUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastVerificationSampleUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DhwCycles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalControlStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CurrentDeviationC = table.Column<double>(type: "double precision", nullable: false),
                    LastDeviationWriteUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CurrentPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FallbackReason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PiIntegral = table.Column<double>(type: "double precision", nullable: false),
                    ManualOverrideUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ManualOverrideDeviationC = table.Column<double>(type: "double precision", nullable: true),
                    ManualOverrideReason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalControlStates", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ThermalEntityConfigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ExpectedUnit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumValid = table.Column<double>(type: "double precision", nullable: true),
                    MaximumValid = table.Column<double>(type: "double precision", nullable: true),
                    MaximumRatePerHour = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalEntityConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalHourlyAggregates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HourUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AverageOutsideTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    AverageRoomTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    AverageLwtC = table.Column<double>(type: "double precision", nullable: true),
                    AverageCop = table.Column<double>(type: "double precision", nullable: true),
                    HeatPumpEnergyKwh = table.Column<double>(type: "double precision", nullable: false),
                    HeatOutputKwh = table.Column<double>(type: "double precision", nullable: false),
                    RoomsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalHourlyAggregates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalModelVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModelType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TrainingFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TrainingToUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    MetricsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalModelVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalPlans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsShadow = table.Column<bool>(type: "boolean", nullable: false),
                    SolverDurationMs = table.Column<int>(type: "integer", nullable: false),
                    ObjectiveCost = table.Column<decimal>(type: "numeric", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    InputSnapshotJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalRoomConfigs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TargetOffsetC = table.Column<double>(type: "double precision", nullable: false),
                    Weight = table.Column<double>(type: "double precision", nullable: false),
                    IsCritical = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumValidC = table.Column<double>(type: "double precision", nullable: false),
                    MaximumValidC = table.Column<double>(type: "double precision", nullable: false),
                    MaximumRateCPerHour = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalRoomConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalSiteConfigs",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ControlMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Legacy"),
                    DhwWriter = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Legacy"),
                    BaseRoomTargetC = table.Column<double>(type: "double precision", nullable: false),
                    LowerComfortBandC = table.Column<double>(type: "double precision", nullable: false),
                    UpperComfortBandC = table.Column<double>(type: "double precision", nullable: false),
                    ActiveDeviationLimitC = table.Column<double>(type: "double precision", nullable: false),
                    TariffEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    HeatPumpPowerSignVerified = table.Column<bool>(type: "boolean", nullable: false),
                    WeatherCurveVerified = table.Column<bool>(type: "boolean", nullable: false),
                    ComfortSetpointConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    ComfortSetpointC = table.Column<double>(type: "double precision", nullable: false),
                    ComfortIntervalDays = table.Column<int>(type: "integer", nullable: false),
                    ComfortFlexibilityDays = table.Column<int>(type: "integer", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Europe/Stockholm"),
                    VariableCostComponentsJson = table.Column<string>(type: "jsonb", nullable: false),
                    TariffDefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalSiteConfigs", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ThermalTelemetrySamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OutsideTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    LeavingWaterTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    ReturnWaterTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    FlowLitresPerMinute = table.Column<double>(type: "double precision", nullable: true),
                    BrineInC = table.Column<double>(type: "double precision", nullable: true),
                    BrineOutC = table.Column<double>(type: "double precision", nullable: true),
                    TankTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    HeatPumpPowerKw = table.Column<double>(type: "double precision", nullable: true),
                    PropertyPowerKw = table.Column<double>(type: "double precision", nullable: true),
                    HeatOutputKw = table.Column<double>(type: "double precision", nullable: true),
                    Cop = table.Column<double>(type: "double precision", nullable: true),
                    DhwActive = table.Column<bool>(type: "boolean", nullable: true),
                    DefrostActive = table.Column<bool>(type: "boolean", nullable: true),
                    BackupHeaterActive = table.Column<bool>(type: "boolean", nullable: true),
                    RoomTemperaturesJson = table.Column<string>(type: "jsonb", nullable: false),
                    QualityJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalTelemetrySamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThermalPlanSteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ThermalPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DesiredHeatOutputKw = table.Column<double>(type: "double precision", nullable: false),
                    DesiredLwtDeviationC = table.Column<double>(type: "double precision", nullable: false),
                    DhwReserved = table.Column<bool>(type: "boolean", nullable: false),
                    DhwMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IncrementalCost = table.Column<decimal>(type: "numeric", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    ExpectedRoomsJson = table.Column<string>(type: "jsonb", nullable: false),
                    DecisionReasonJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThermalPlanSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ThermalPlanSteps_ThermalPlans_ThermalPlanId",
                        column: x => x.ThermalPlanId,
                        principalTable: "ThermalPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DhwCycles_UserId_PlannedStartUtc",
                table: "DhwCycles",
                columns: new[] { "UserId", "PlannedStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalEntityConfigs_UserId_EntityId",
                table: "ThermalEntityConfigs",
                columns: new[] { "UserId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalEntityConfigs_UserId_Role",
                table: "ThermalEntityConfigs",
                columns: new[] { "UserId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThermalEvents_UserId_TimestampUtc",
                table: "ThermalEvents",
                columns: new[] { "UserId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalHourlyAggregates_UserId_HourUtc",
                table: "ThermalHourlyAggregates",
                columns: new[] { "UserId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThermalModelVersions_UserId_ModelType_CreatedAtUtc",
                table: "ThermalModelVersions",
                columns: new[] { "UserId", "ModelType", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalPlans_UserId_CreatedAtUtc",
                table: "ThermalPlans",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalPlanSteps_ThermalPlanId_StartUtc",
                table: "ThermalPlanSteps",
                columns: new[] { "ThermalPlanId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ThermalRoomConfigs_UserId_EntityId",
                table: "ThermalRoomConfigs",
                columns: new[] { "UserId", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThermalTelemetrySamples_UserId_TimestampUtc",
                table: "ThermalTelemetrySamples",
                columns: new[] { "UserId", "TimestampUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DhwCycles");

            migrationBuilder.DropTable(
                name: "ThermalControlStates");

            migrationBuilder.DropTable(
                name: "ThermalEntityConfigs");

            migrationBuilder.DropTable(
                name: "ThermalEvents");

            migrationBuilder.DropTable(
                name: "ThermalHourlyAggregates");

            migrationBuilder.DropTable(
                name: "ThermalModelVersions");

            migrationBuilder.DropTable(
                name: "ThermalPlanSteps");

            migrationBuilder.DropTable(
                name: "ThermalRoomConfigs");

            migrationBuilder.DropTable(
                name: "ThermalSiteConfigs");

            migrationBuilder.DropTable(
                name: "ThermalTelemetrySamples");

            migrationBuilder.DropTable(
                name: "ThermalPlans");
        }
    }
}
