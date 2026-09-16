using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class CliWorkflowTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-cli-tests";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-cli-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Agent_can_acquire_note_and_resolve_while_stale_writes_fail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var port = AvailablePort();
        var baseUrl = $"http://localhost:{port}";
        var api = StartApi(root, port);
        var standardOutput = api.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = api.StandardError.ReadToEndAsync(cancellationToken);
        var cliDirectory = Path.Combine(Path.GetTempPath(), $"helpaffe-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cliDirectory);
        var cliPath = Path.Combine(cliDirectory, OperatingSystem.IsWindows() ? "helpaffe.exe" : "helpaffe");

        try
        {
            await WaitUntilReady(baseUrl, api, cancellationToken);
            var (projectId, hiddenProjectId, token) = await ProvisionAgent(baseUrl, cancellationToken);
            await SeedTickets(projectId, hiddenProjectId, cancellationToken);
            await BuildCli(root, cliPath, cancellationToken);

            var searched = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "list", "--project", projectId.ToString(), "--status", "open", "--search", "workflow"],
                null, cancellationToken);
            Assert.Equal(0, searched.ExitCode);
            using var searchedJson = JsonDocument.Parse(searched.StandardOutput);
            var searchedItems = searchedJson.RootElement.GetProperty("items");
            Assert.Equal(2, searchedItems.GetArrayLength());
            Assert.DoesNotContain(searchedItems.EnumerateArray(),
                value => value.GetProperty("number").GetString() == "HLP-HIDDEN-1");

            var context = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "get", "HLP-CLI-1"], null, cancellationToken);
            Assert.Equal(0, context.ExitCode);
            using var contextJson = JsonDocument.Parse(context.StandardOutput);
            Assert.Equal("2.4.1", contextJson.RootElement.GetProperty("context").GetProperty("release").GetString());

            var related = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "requester-tickets", "HLP-CLI-1"], null, cancellationToken);
            Assert.Equal(0, related.ExitCode);
            using var relatedJson = JsonDocument.Parse(related.StandardOutput);
            var relatedItems = relatedJson.RootElement.GetProperty("items");
            Assert.Single(relatedItems.EnumerateArray());
            Assert.Equal("HLP-CLI-2", relatedItems[0].GetProperty("number").GetString());

            var hidden = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "get", "HLP-HIDDEN-1"], null, cancellationToken);
            Assert.Equal(3, hidden.ExitCode);
            Assert.Empty(hidden.StandardOutput);

            var acquired = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "next", "--project", projectId.ToString()], null, cancellationToken);
            Assert.Equal(0, acquired.ExitCode);
            using var acquiredJson = JsonDocument.Parse(acquired.StandardOutput);
            var acquiredSummary = acquiredJson.RootElement.GetProperty("summary");
            Assert.Equal("HLP-CLI-1", acquiredSummary.GetProperty("number").GetString());
            var acquiredVersion = acquiredSummary.GetProperty("version").GetInt32();

            var noted = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "note", "HLP-CLI-1", "--version", acquiredVersion.ToString(), "--note-file", "-"],
                "Investigated through stdin.\r\n", cancellationToken);
            Assert.Equal(0, noted.ExitCode);
            using var notedJson = JsonDocument.Parse(noted.StandardOutput);
            var notedVersion = notedJson.RootElement.GetProperty("summary").GetProperty("version").GetInt32();

            var resolved = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "reply", "HLP-CLI-1", "--version", notedVersion.ToString(), "--status", "resolved", "--message-file", "-"],
                "This is resolved.\n", cancellationToken);
            Assert.Equal(0, resolved.ExitCode);
            using var resolvedJson = JsonDocument.Parse(resolved.StandardOutput);
            Assert.Equal("resolved", resolvedJson.RootElement.GetProperty("summary").GetProperty("status").GetString());

            var stale = await RunCli(cliPath, baseUrl, token,
                ["--json", "ticket", "reopen", "HLP-CLI-1", "--version", acquiredVersion.ToString()], null, cancellationToken);
            Assert.Equal(6, stale.ExitCode);
            Assert.Empty(stale.StandardOutput);
            using var staleJson = JsonDocument.Parse(stale.StandardError);
            Assert.Equal("/problems/stale", staleJson.RootElement.GetProperty("type").GetString());
            Assert.True(staleJson.RootElement.GetProperty("current_version").GetInt32() > acquiredVersion);

            await using var database = CreateDatabase();
            var ticket = await database.Tickets.Include(value => value.Conversation)
                .SingleAsync(value => value.Number == "HLP-CLI-1", cancellationToken);
            Assert.Equal(TicketStatus.Resolved, ticket.Status);
            Assert.Contains(ticket.Conversation, value =>
                value.Kind is ConversationEntryKind.InternalNote && value.Body == "Investigated through stdin.");
            Assert.Contains(ticket.Conversation, value =>
                value.Kind is ConversationEntryKind.PublicReply && value.Body == "This is resolved.");
        }
        finally
        {
            if (!api.HasExited)
            {
                api.Kill(entireProcessTree: true);
                await api.WaitForExitAsync(cancellationToken);
            }
            await standardOutput;
            await standardError;
            api.Dispose();
            Directory.Delete(cliDirectory, recursive: true);
        }
    }

    private Process StartApi(string root, int port)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(root, "src", "Helpaffe.Api"));
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ConnectionStrings__Database"] = _postgres.GetConnectionString();
        start.Environment["Bootstrap__Name"] = "CLI Admin";
        start.Environment["Bootstrap__Email"] = "cli-admin@example.test";
        start.Environment["Bootstrap__Password"] = AdminPassword;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the API process.");
    }

    private static async Task WaitUntilReady(string baseUrl, Process api, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (api.HasExited) throw new InvalidOperationException($"The API exited with code {api.ExitCode}.");
            try
            {
                using var response = await client.GetAsync("/health/ready", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // The listener is not ready yet.
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("The API did not become ready.");
    }

    private static async Task<(Guid ProjectId, Guid HiddenProjectId, string Token)> ProvisionAgent(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        using var signedIn = await client.PostAsJsonAsync("/api/backoffice/session", new
        {
            email = "cli-admin@example.test",
            password = AdminPassword,
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        client.DefaultRequestHeaders.Add("X-Helpaffe-CSRF", "1");

        using var projectResponse = await client.PostAsJsonAsync("/api/backoffice/projects", new
        {
            key = "CLI",
            name = "CLI integration",
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var projectId = project.GetProperty("id").GetGuid();

        using var hiddenProjectResponse = await client.PostAsJsonAsync("/api/backoffice/projects", new
        {
            key = "HIDDEN",
            name = "Hidden integration",
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, hiddenProjectResponse.StatusCode);
        var hiddenProject = await hiddenProjectResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var hiddenProjectId = hiddenProject.GetProperty("id").GetGuid();

        using var supportResponse = await client.PostAsJsonAsync("/api/backoffice/users", new
        {
            name = "Restricted CLI support",
            email = "restricted-cli@example.test",
            password = "restricted-cli-password",
            role = "support",
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, supportResponse.StatusCode);
        var support = await supportResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var supportId = support.GetProperty("id").GetGuid();
        using var grantResponse = await client.PutAsync(
            $"/api/backoffice/users/{supportId}/projects/{projectId}", null, cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, grantResponse.StatusCode);

        using var agentResponse = await client.PostAsJsonAsync("/api/backoffice/agents", new
        {
            name = "CLI integration agent",
            user_id = supportId,
            all_projects = true,
            project_ids = Array.Empty<Guid>(),
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, agentResponse.StatusCode);
        var agent = await agentResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (projectId, hiddenProjectId, agent.GetProperty("token").GetString()!);
    }

    private async Task SeedTickets(Guid projectId, Guid hiddenProjectId, CancellationToken cancellationToken)
    {
        await using var database = CreateDatabase();
        database.Tickets.Add(Ticket.Create(
            Guid.NewGuid(),
            "HLP-CLI-1",
            projectId,
            "CLI workflow",
            "cli-requester",
            "CLI Requester",
            "cli-requester@example.test",
            "Please prove the whole workflow.",
            DateTimeOffset.UtcNow.AddMinutes(-3),
            contextJson: "{\"release\":\"2.4.1\",\"page\":\"/settings\"}"));
        database.Tickets.Add(Ticket.Create(
            Guid.NewGuid(),
            "HLP-CLI-2",
            projectId,
            "Previous CLI workflow",
            "cli-requester",
            "CLI Requester",
            "cli-requester@example.test",
            "This is another ticket in the same project.",
            DateTimeOffset.UtcNow.AddMinutes(-2)));
        database.Tickets.Add(Ticket.Create(
            Guid.NewGuid(),
            "HLP-HIDDEN-1",
            hiddenProjectId,
            "Hidden CLI workflow",
            "cli-requester",
            "CLI Requester",
            "cli-requester@example.test",
            "This ticket must remain outside the support user's scope.",
            DateTimeOffset.UtcNow.AddMinutes(-1)));
        await database.SaveChangesAsync(cancellationToken);
    }

    private HelpaffeDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<HelpaffeDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new HelpaffeDbContext(options);
    }

    private static async Task BuildCli(string root, string cliPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("go")
        {
            WorkingDirectory = Path.Combine(root, "src", "cli"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(cliPath);
        start.ArgumentList.Add("./cmd/helpaffe");
        var result = await RunProcess(start, null, cancellationToken);
        Assert.True(result.ExitCode == 0, $"go build failed:\n{result.StandardOutput}\n{result.StandardError}");
    }

    private static Task<ProcessResult> RunCli(
        string cliPath,
        string baseUrl,
        string token,
        IReadOnlyList<string> arguments,
        string? input,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.Environment["HELPAFFE_URL"] = baseUrl;
        start.Environment["HELPAFFE_TOKEN"] = token;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return RunProcess(start, input, cancellationToken);
    }

    private static async Task<ProcessResult> RunProcess(
        ProcessStartInfo start,
        string? input,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        if (start.RedirectStandardInput)
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Helpaffe.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
