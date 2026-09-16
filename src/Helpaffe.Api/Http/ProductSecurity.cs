using Helpaffe.Infrastructure.Identity;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Helpaffe.Api.Http;

public sealed record ProductActor(ProductApiKeyRecord Credential, ProjectRecord Project);

public static class ProductSecurity
{
    private const string ActorItem = "helpaffe.product-actor";

    public static ProductActor? ProductActor(this HttpContext context) =>
        context.Items.TryGetValue(ActorItem, out var value) ? value as ProductActor : null;

    public static async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api/product"))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer hfp_", StringComparison.Ordinal))
        {
            await BackofficeSecurity.Problem(context, 401, "unauthenticated", "A product API key is required.");
            return;
        }

        var factory = context.RequestServices.GetRequiredService<IDbContextFactory<HelpaffeDbContext>>();
        await using var database = await factory.CreateDbContextAsync(context.RequestAborted);
        var hash = AccessToken.Hash(authorization[7..]);
        var credential = await database.ProductApiKeys.AsNoTracking().SingleOrDefaultAsync(
            value => value.TokenHash == hash && value.RevokedAt == null,
            context.RequestAborted);
        var project = credential is null ? null : await database.Projects.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == credential.ProjectId,
            context.RequestAborted);
        if (credential is null || project is null)
        {
            await BackofficeSecurity.Problem(context, 401, "unauthenticated", "The product API key is unknown or revoked.");
            return;
        }

        context.Items[ActorItem] = new ProductActor(credential, project);
        await next(context);
    }
}
