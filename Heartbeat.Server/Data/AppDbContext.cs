using Microsoft.EntityFrameworkCore;
using server.Entities;

namespace server.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<AppUsage> AppUsages => Set<AppUsage>();
        public DbSet<AppIcon> AppIcons => Set<AppIcon>();

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Device>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.DeviceName)
                    .IsUnique();
            });

            modelBuilder.Entity<AppUsage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.DeviceName);
                entity.HasIndex(e => e.StartTime);

                // 复合索引：用于合并查询时快速查找同设备+同应用的最新记录
                entity.HasIndex(e => new { e.DeviceName, e.AppName, e.EndTime });
            });

            modelBuilder.Entity<AppIcon>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.AppName)
                    .IsUnique();
            });
        }
    }
}
