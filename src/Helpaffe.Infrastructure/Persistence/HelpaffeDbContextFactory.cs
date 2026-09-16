using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpaffe.Infrastructure.Persistence;

public sealed class HelpaffeDbContextFactory : IDesignTimeDbContextFactory<HelpaffeDbContext>
{
    public HelpaffeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Database")
            ?? "Host=localhost;Port=56632;Database=helpaffe;Username=helpaffe;Password=helpaffe";
        var options = new DbContextOptionsBuilder<HelpaffeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new HelpaffeDbContext(options);
    }
}
