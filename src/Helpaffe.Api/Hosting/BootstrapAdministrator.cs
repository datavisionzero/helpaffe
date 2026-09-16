using Helpaffe.Domain.Identity;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Hosting;

public static class BootstrapAdministrator
{
    public static async Task EnsureAsync(IServiceProvider services, IConfiguration configuration)
    {
        var email = configuration["Bootstrap:Email"];
        var password = configuration["Bootstrap:Password"];
        var name = configuration["Bootstrap:Name"] ?? "Administrator";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var factory = services.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>();
        await using var database = await factory.CreateDbContextAsync();
        if (await database.Users.AnyAsync())
        {
            return;
        }

        database.Users.Add(new UserRecord
        {
            Id = Guid.NewGuid(),
            Email = email.Trim(),
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            PasswordHash = PasswordHash.Create(password),
            Role = UserRole.Administrator,
        });
        await database.SaveChangesAsync();
    }
}
