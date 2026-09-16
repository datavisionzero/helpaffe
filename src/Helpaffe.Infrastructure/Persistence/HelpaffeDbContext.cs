using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Infrastructure.Persistence;

public sealed class HelpaffeDbContext(DbContextOptions<HelpaffeDbContext> options)
    : DbContext(options)
{
    public DbSet<FoundationState> FoundationStates => Set<FoundationState>();

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
    }
}
