using Microsoft.EntityFrameworkCore;
using NetVigil.Shared;

namespace NetVigil.Server.Data
{
    public class NetVigilDbContext : DbContext
    {
        public NetVigilDbContext(DbContextOptions<NetVigilDbContext> options) : base(options) { }

        public DbSet<NetworkDevice> Devices => Set<NetworkDevice>();
        public DbSet<TrafficSample> TrafficSamples => Set<TrafficSample>();
        public DbSet<AnomalyEvent> Anomalies => Set<AnomalyEvent>();
        public DbSet<User> Users => Set<User>();
        public DbSet<SettingEntry> Settings => Set<SettingEntry>();
        public DbSet<DeviceFlagAudit> DeviceFlagAudits => Set<DeviceFlagAudit>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NetworkDevice>(b =>
            {
                b.HasKey(d => d.MacAddress);
                b.Property(d => d.MacAddress).HasMaxLength(32);
                b.Property(d => d.IpAddress).HasMaxLength(64);
                b.Property(d => d.Hostname).HasMaxLength(256);
                b.Property(d => d.Vendor).HasMaxLength(128);
                b.Property(d => d.Type).HasMaxLength(64);
                b.Property(d => d.FlaggedBy).HasMaxLength(64);
            });

            modelBuilder.Entity<DeviceFlagAudit>(b =>
            {
                b.HasKey(a => a.Id);
                b.Property(a => a.DeviceMac).HasMaxLength(32);
                b.Property(a => a.ChangedBy).HasMaxLength(64);
                b.Property(a => a.Reason).HasMaxLength(256);
                b.HasIndex(a => new { a.DeviceMac, a.ChangedAt });
                b.HasIndex(a => a.ChangedAt);
            });

            modelBuilder.Entity<TrafficSample>(b =>
            {
                b.HasKey(s => s.Id);
                b.Property(s => s.DeviceMac).HasMaxLength(32);
                b.HasIndex(s => new { s.DeviceMac, s.Timestamp });
                b.HasIndex(s => s.Timestamp);
            });

            modelBuilder.Entity<AnomalyEvent>(b =>
            {
                b.HasKey(a => a.Id);
                b.Property(a => a.DeviceMac).HasMaxLength(32);
                b.Property(a => a.DeviceName).HasMaxLength(256);
                b.Property(a => a.Description).HasMaxLength(512);
                b.HasIndex(a => a.Timestamp);
                b.HasIndex(a => new { a.DeviceMac, a.Timestamp });
            });

            modelBuilder.Entity<User>(b =>
            {
                b.HasKey(u => u.Id);
                b.Property(u => u.Username).HasMaxLength(64);
                b.HasIndex(u => u.Username).IsUnique();
                b.Property(u => u.PasswordHash).HasMaxLength(256);
            });

            modelBuilder.Entity<SettingEntry>(b =>
            {
                b.HasKey(s => s.Key);
                b.Property(s => s.Key).HasMaxLength(64);
                b.Property(s => s.Value).HasMaxLength(2048);
            });
        }
    }

    public class SettingEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}
