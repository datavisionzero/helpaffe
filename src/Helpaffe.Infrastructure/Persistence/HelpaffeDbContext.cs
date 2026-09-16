using Microsoft.EntityFrameworkCore;
using Helpaffe.Infrastructure.Identity;

namespace Helpaffe.Infrastructure.Persistence;

public sealed class HelpaffeDbContext(DbContextOptions<HelpaffeDbContext> options)
    : DbContext(options)
{
    public DbSet<FoundationState> FoundationStates => Set<FoundationState>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<UserProjectAccessRecord> UserProjectAccess => Set<UserProjectAccessRecord>();
    public DbSet<BrowserSessionRecord> BrowserSessions => Set<BrowserSessionRecord>();
    public DbSet<AgentCredentialRecord> AgentCredentials => Set<AgentCredentialRecord>();
    public DbSet<AgentProjectAccessRecord> AgentProjectAccess => Set<AgentProjectAccessRecord>();
    public DbSet<ProductApiKeyRecord> ProductApiKeys => Set<ProductApiKeyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var foundation = modelBuilder.Entity<FoundationState>();
        foundation.ToTable("foundation_state");
        foundation.HasKey(state => state.Id);
        foundation.Property(state => state.Id).ValueGeneratedNever();
        foundation.Property(state => state.CreatedAt).HasColumnName("created_at");

        foundation.HasData(new FoundationState
        {
            Id = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
        });

        var user = modelBuilder.Entity<UserRecord>();
        user.ToTable("users");
        user.HasKey(value => value.Id);
        user.Property(value => value.Email).HasMaxLength(320);
        user.Property(value => value.NormalizedEmail).HasMaxLength(320);
        user.HasIndex(value => value.NormalizedEmail).IsUnique();
        user.Property(value => value.Name).HasMaxLength(200);
        user.Property(value => value.PasswordHash).HasColumnName("password_hash");
        user.Property(value => value.Role).HasConversion<string>().HasMaxLength(32);

        var project = modelBuilder.Entity<ProjectRecord>();
        project.ToTable("projects");
        project.HasKey(value => value.Id);
        project.Property(value => value.Key).HasMaxLength(32);
        project.HasIndex(value => value.Key).IsUnique();
        project.Property(value => value.Name).HasMaxLength(200);

        var access = modelBuilder.Entity<UserProjectAccessRecord>();
        access.ToTable("user_project_access");
        access.HasKey(value => new { value.UserId, value.ProjectId });
        access.HasOne<UserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);
        access.HasOne<ProjectRecord>().WithMany().HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade);

        var session = modelBuilder.Entity<BrowserSessionRecord>();
        session.ToTable("browser_sessions");
        session.HasKey(value => value.Id);
        session.Property(value => value.TokenHash).HasMaxLength(64);
        session.HasIndex(value => value.TokenHash).IsUnique();
        session.HasOne<UserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);

        var agent = modelBuilder.Entity<AgentCredentialRecord>();
        agent.ToTable("agent_credentials");
        agent.HasKey(value => value.Id);
        agent.Property(value => value.Name).HasMaxLength(200);
        agent.Property(value => value.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        agent.Property(value => value.TokenPrefix).HasColumnName("token_prefix").HasMaxLength(12);
        agent.HasIndex(value => value.TokenHash).IsUnique();
        agent.HasOne<UserRecord>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.Cascade);

        var agentAccess = modelBuilder.Entity<AgentProjectAccessRecord>();
        agentAccess.ToTable("agent_project_access");
        agentAccess.HasKey(value => new { value.AgentCredentialId, value.ProjectId });
        agentAccess.HasOne<AgentCredentialRecord>().WithMany().HasForeignKey(value => value.AgentCredentialId).OnDelete(DeleteBehavior.Cascade);
        agentAccess.HasOne<ProjectRecord>().WithMany().HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade);

        var productKey = modelBuilder.Entity<ProductApiKeyRecord>();
        productKey.ToTable("product_api_keys");
        productKey.HasKey(value => value.Id);
        productKey.Property(value => value.Name).HasMaxLength(200);
        productKey.Property(value => value.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        productKey.Property(value => value.TokenPrefix).HasColumnName("token_prefix").HasMaxLength(12);
        productKey.HasIndex(value => value.TokenHash).IsUnique();
        productKey.HasOne<ProjectRecord>().WithMany().HasForeignKey(value => value.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
