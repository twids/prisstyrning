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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // UserSettings
        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Zone).HasMaxLength(10).HasDefaultValue("SE3");
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
    }
}
