using Microsoft.EntityFrameworkCore;
using server.Entities;

namespace server.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<AppUsage> AppUsages => Set<AppUsage>();

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
            });
        }
    }
}
