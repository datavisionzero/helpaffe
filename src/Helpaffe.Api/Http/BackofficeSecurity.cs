using System.Security.Cryptography;
using System.Text;
using Helpaffe.Domain.Identity;
using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public static class BackofficeSecurity
{
    public const string CookieName = "helpaffe_session";
    public const string UserItem = "helpaffe.user";

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static UserRecord? User(this HttpContext context) =>
        context.Items.TryGetValue(UserItem, out var value) ? value as UserRecord : null;

    public static bool IsAdministrator(this HttpContext context) =>
        context.User()?.Role is UserRole.Administrator;

    public static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api/backoffice") ||
            context.Request.Path == "/api/backoffice/version" ||
            context.Request.Path == "/api/backoffice/session" && context.Request.Method == "POST")
        {
            await next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var token))
        {
            await Problem(context, 401, "unauthenticated", "Sign in is required.");
            return;
        }

        var factory = context.RequestServices.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var hash = HashToken(token);
        var session = await database.BrowserSessions.SingleOrDefaultAsync(
            value => value.TokenHash == hash && value.ExpiresAt > DateTimeOffset.UtcNow,
            context.RequestAborted);
        var user = session is null ? null : await database.Users.FindAsync([session.UserId], context.RequestAborted);
        if (user is null || !user.IsActive)
        {
            context.Response.Cookies.Delete(CookieName);
            await Problem(context, 401, "unauthenticated", "The session is no longer active.");
            return;
        }

        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
            context.Request.Headers["X-Helpaffe-CSRF"] != "1")
        {
            await Problem(context, 403, "csrf", "The CSRF header is required.");
            return;
        }

        context.Items[UserItem] = user;
        await next(context);
    }

    public static Task Problem(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new
        {
            type = $"/problems/{code}",
            title = code.Replace('-', ' '),
            status,
            detail,
            instance = context.Request.Path.Value,
        });
    }
}
