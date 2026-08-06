using LabLock.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace LabLock.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<CommandHistory> CommandHistories => Set<CommandHistory>();
    public DbSet<AiSettings> AiSettings => Set<AiSettings>();
    public DbSet<AiConversation> AiConversations => Set<AiConversation>();
    public DbSet<AiMessage> AiMessages => Set<AiMessage>();
    public DbSet<DashboardUser> DashboardUsers => Set<DashboardUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasKey(e => e.ClientId);
        });

        modelBuilder.Entity<ActivityLog>(entity =>
        {
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.EventType);
        });

        modelBuilder.Entity<CommandHistory>(entity =>
        {
            entity.HasIndex(e => e.ClientId);
            entity.HasIndex(e => e.SentAt);
        });

        modelBuilder.Entity<AiConversation>(entity =>
        {
            entity.HasMany(e => e.Messages)
                  .WithOne(e => e.Conversation)
                  .HasForeignKey(e => e.ConversationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiMessage>(entity =>
        {
            entity.HasIndex(e => e.ConversationId);
        });
    }
}
