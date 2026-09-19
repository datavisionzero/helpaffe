using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Helpaffe.Api.Hosting;

public static class LogaffeLoggingExtensions
{
    public static IHostApplicationBuilder AddHelpaffeLogging(this IHostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        var url = builder.Configuration["Observability:Logaffe:Url"];
        var token = builder.Configuration["Observability:Logaffe:IngestToken"];
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(token))
            return builder;

        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "Observability:Logaffe:IngestToken requires Observability:Logaffe:Url.");

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Observability:Logaffe:Url requires Observability:Logaffe:IngestToken.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var installation) ||
            (installation.Scheme != Uri.UriSchemeHttp && installation.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"Observability:Logaffe:Url must be an absolute http or https address: '{url}'.");

        builder.Logging.AddLogaffe(options =>
        {
            options.Installation = installation;
            options.IngestToken = token;
            options.Instance = Environment.MachineName;
            options.IncludeScopes = true;
            options.OnFailure = (message, exception) =>
                Console.Error.WriteLine(exception is null ? message : $"{message} {exception}");
        });

        return builder;
    }
}
