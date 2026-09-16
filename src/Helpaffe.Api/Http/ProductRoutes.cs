namespace Helpaffe.Api.Http;

public static class ProductRoutes
{
    public static IEndpointRouteBuilder MapProduct(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/product/project", (HttpContext context) =>
        {
            var project = context.ProductActor()!.Project;
            return Results.Ok(new { project.Id, project.Key, project.Name });
        });
        return endpoints;
    }
}
