using Microsoft.EntityFrameworkCore;
using Prisstyrning.Data.Entities;

namespace Prisstyrning.Data;

public class PrisstyrningDbContext : DbContext
{
    public PrisstyrningDbContext(DbContextOptions<PrisstyrningDbContext> options) : base(options) { }

    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<AdminRole> AdminRoles => Set<AdminRole>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<ScheduleHistoryEntry> ScheduleHistory => Set<ScheduleHistoryEntry>();
    public DbSet<DaikinToken> DaikinTokens => Set<DaikinToken>();
    public DbSet<FlexibleScheduleState> FlexibleScheduleStates => Set<FlexibleScheduleState>();
    public DbSet<ThermalSiteConfig> ThermalSiteConfigs => Set<ThermalSiteConfig>();
    public DbSet<ThermalRoomConfig> ThermalRoomConfigs => Set<ThermalRoomConfig>();
    public DbSet<ThermalEntityConfig> ThermalEntityConfigs => Set<ThermalEntityConfig>();
    public DbSet<ThermalTelemetrySample> ThermalTelemetrySamples => Set<ThermalTelemetrySample>();
    public DbSet<ThermalPlan> ThermalPlans => Set<ThermalPlan>();
    public DbSet<ThermalPlanStep> ThermalPlanSteps => Set<ThermalPlanStep>();
    public DbSet<ThermalModelVersion> ThermalModelVersions => Set<ThermalModelVersion>();
    public DbSet<ThermalEvent> ThermalEvents => Set<ThermalEvent>();
    public DbSet<DhwCycle> DhwCycles => Set<DhwCycle>();
    public DbSet<ThermalControlState> ThermalControlStates => Set<ThermalControlState>();
    public DbSet<ThermalControlCommand> ThermalControlCommands => Set<ThermalControlCommand>();
    public DbSet<ThermalHourlyAggregate> ThermalHourlyAggregates => Set<ThermalHourlyAggregate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // UserSettings
        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Zone).HasMaxLength(10).HasDefaultValue("SE3");
            e.Property(x => x.SchedulingMode).HasMaxLength(20).HasDefaultValue("Classic");
            e.Property(x => x.EcoIntervalHours).HasDefaultValue(24);
            e.Property(x => x.EcoFlexibilityHours).HasDefaultValue(12);
            e.Property(x => x.ComfortIntervalDays).HasDefaultValue(21);
            e.Property(x => x.ComfortFlexibilityDays).HasDefaultValue(7);
            e.Property(x => x.ComfortEarlyPercentile).HasDefaultValueSql("0.1");
            e.Property(x => x.Timezone).HasDefaultValue("auto");
        });

        // FlexibleScheduleState
        modelBuilder.Entity<FlexibleScheduleState>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
        });

        // AdminRole
        modelBuilder.Entity<AdminRole>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
        });

        // PriceSnapshot
        modelBuilder.Entity<PriceSnapshot>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Zone).HasMaxLength(10).IsRequired();
            e.Property(x => x.TodayPricesJson).HasColumnType("jsonb");
            e.Property(x => x.TomorrowPricesJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.Zone, x.Date });
        });

        // ScheduleHistoryEntry
        modelBuilder.Entity<ScheduleHistoryEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.SchedulePayloadJson).HasColumnType("jsonb");
            e.HasIndex(x => x.UserId);
        });

        // DaikinToken
        modelBuilder.Entity<DaikinToken>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
        });

        modelBuilder.Entity<ThermalSiteConfig>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.ControlMode).HasMaxLength(20).HasDefaultValue("Legacy");
            e.Property(x => x.DhwWriter).HasMaxLength(20).HasDefaultValue("Legacy");
            e.Property(x => x.DhwLeaseOwner).HasMaxLength(150);
            e.Property(x => x.TimeZone).HasMaxLength(100).HasDefaultValue("Europe/Stockholm");
            e.Property(x => x.VariableCostComponentsJson).HasColumnType("jsonb");
            e.Property(x => x.TariffDefinitionJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ThermalRoomConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.UserId, x.EntityId }).IsUnique();
        });

        modelBuilder.Entity<ThermalEntityConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(255).IsRequired();
            e.Property(x => x.ExpectedUnit).HasMaxLength(50);
            e.HasIndex(x => new { x.UserId, x.Role }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.EntityId });
        });

        modelBuilder.Entity<ThermalTelemetrySample>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RoomTemperaturesJson).HasColumnType("jsonb");
            e.Property(x => x.QualityJson).HasColumnType("jsonb");
            e.Property(x => x.OutsideTemperatureForecastJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.TimestampUtc }).IsUnique();
        });

        modelBuilder.Entity<ThermalPlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.InputSnapshotJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<ThermalPlanStep>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DhwMode).HasMaxLength(20);
            e.Property(x => x.ExpectedRoomsJson).HasColumnType("jsonb");
            e.Property(x => x.DecisionReasonJson).HasColumnType("jsonb");
            e.HasOne(x => x.ThermalPlan)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.ThermalPlanId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ThermalPlanId, x.StartUtc });
        });

        modelBuilder.Entity<ThermalModelVersion>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.ModelType).HasMaxLength(50).IsRequired();
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.Property(x => x.MetricsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.ModelType, x.CreatedAtUtc });
        });

        modelBuilder.Entity<ThermalEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Severity).HasMaxLength(30);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.TimestampUtc });
        });

        modelBuilder.Entity<DhwCycle>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.Status).HasMaxLength(30);
            e.Property(x => x.PowerProfileJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.PlannedStartUtc });
        });

        modelBuilder.Entity<ThermalControlState>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.FallbackReason).HasMaxLength(255);
            e.Property(x => x.LeaseOwner).HasMaxLength(100);
            e.Property(x => x.ManualOverrideReason).HasMaxLength(255);
        });

        modelBuilder.Entity<ThermalControlCommand>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.CommandType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Target).HasMaxLength(255).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.UserId, x.TimestampUtc });
        });

        modelBuilder.Entity<ThermalHourlyAggregate>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.RoomsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.UserId, x.HourUtc }).IsUnique();
        });
    }
}
