using Helpaffe.Api.Hosting;
using Logaffe.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Helpaffe.IntegrationTests;

public sealed class LogaffeLoggingConfigurationTests
{
    [Fact]
    public void Unconfigured_logging_keeps_console_without_logaffe()
    {
        using var host = BuildHost([]);

        Assert.Contains(host.Services.GetServices<ILoggerProvider>(), provider => provider is ConsoleLoggerProvider);
        Assert.DoesNotContain(host.Services.GetServices<ILoggerProvider>(), provider => provider is LogaffeLoggerProvider);
    }

    [Fact]
    public void Url_and_token_add_logaffe_without_removing_console()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Observability:Logaffe:Url"] = "https://logs.example.com",
            ["Observability:Logaffe:IngestToken"] = "test-token",
        });

        Assert.Contains(host.Services.GetServices<ILoggerProvider>(), provider => provider is ConsoleLoggerProvider);
        Assert.Contains(host.Services.GetServices<ILoggerProvider>(), provider => provider is LogaffeLoggerProvider);
    }

    [Theory]
    [InlineData("Observability:Logaffe:Url", "Observability:Logaffe:IngestToken")]
    [InlineData("Observability:Logaffe:IngestToken", "Observability:Logaffe:Url")]
    public void Incomplete_configuration_fails_at_startup(string configuredKey, string missingKey)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildHost(new Dictionary<string, string?>
        {
            [configuredKey] = "set",
        }));

        Assert.Contains(missingKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("logs.example.com")]
    [InlineData("ftp://logs.example.com")]
    public void Non_http_url_fails_at_startup(string url)
    {
        var error = Assert.Throws<InvalidOperationException>(() => BuildHost(new Dictionary<string, string?>
        {
            ["Observability:Logaffe:Url"] = url,
            ["Observability:Logaffe:IngestToken"] = "test-token",
        }));

        Assert.Contains("Observability:Logaffe:Url", error.Message, StringComparison.Ordinal);
    }

    private static IHost BuildHost(IEnumerable<KeyValuePair<string, string?>> configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHelpaffeLogging();
        return builder.Build();
    }
}
