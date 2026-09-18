using Helpaffe.Api.Hosting;
using Helpaffe.Api.Http;
using Helpaffe.Application.Attachments;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Attachments;
using Helpaffe.Infrastructure.Persistence;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");

builder.Services.AddDbContextFactory<HelpaffeDbContext>(options => options.UseNpgsql(connectionString));
var attachmentRoot = builder.Configuration["Attachments:RootPath"];
if (string.IsNullOrWhiteSpace(attachmentRoot))
    attachmentRoot = Path.Combine(builder.Environment.ContentRootPath, "data", "attachments");
builder.Services.AddSingleton<IAttachmentStorage>(new LocalAttachmentStorage(attachmentRoot));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = TicketAttachment.MaximumTotalSize + 1024 * 1024;
    options.ValueLengthLimit = 64 * 1024;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddSingleton<TicketWorkNotifier>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
});
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);

var app = builder.Build();
var serverVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

await using (var scope = app.Services.CreateAsyncScope())
{
    await using var database = await scope.ServiceProvider
        .GetRequiredService<IDbContextFactory<HelpaffeDbContext>>()
        .CreateDbContextAsync();
    await database.Database.MigrateAsync();
}
await BootstrapAdministrator.EnsureAsync(app.Services, app.Configuration);

app.Use(async (context, next) =>
{
    context.Response.Headers["Helpaffe-Version"] = serverVersion;
    await next(context);
});
app.Use(BackofficeSecurity.AuthenticateAsync);
app.Use(ProductSecurity.AuthenticateAsync);

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

app.MapGet("/api/backoffice/version", () => Results.Ok(new
{
    version = serverVersion,
}));
app.MapBackoffice();
app.MapTickets();
app.MapSolutions();
app.MapProduct();
app.MapNotificationConfiguration();
app.MapNotifications();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;
