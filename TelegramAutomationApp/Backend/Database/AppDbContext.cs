using Microsoft.EntityFrameworkCore;
using TelegramAutomationApp.Backend.Models;

namespace TelegramAutomationApp.Backend.Database
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<TelegramAccount> Accounts => Set<TelegramAccount>();
        public DbSet<CampaignTask> CampaignTasks => Set<CampaignTask>();
        public DbSet<TaskTargetItem> TaskTargetItems => Set<TaskTargetItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TelegramAccount>()
                .HasIndex(a => a.PhoneNumber)
                .IsUnique();

            modelBuilder.Entity<TaskTargetItem>()
                .HasOne(t => t.CampaignTask)
                .WithMany(c => c.Targets)
                .HasForeignKey(t => t.CampaignTaskId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
