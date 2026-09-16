using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Helpaffe.Domain.Tickets;
using Helpaffe.Infrastructure.Persistence;
using Helpaffe.Sdk;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Helpaffe.IntegrationTests;

public sealed class CliWorkflowTests : IAsyncLifetime
{
    private const string AdminPassword = "admin-password-for-cli-tests";
    private static readonly string EncryptionKey = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("helpaffe")
        .WithUsername("helpaffe")
        .WithPassword("helpaffe-cli-tests")
        .Build();

    public ValueTask InitializeAsync() => new(_postgres.StartAsync());

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Two_products_sdk_agent_cli_web_contract_and_email_complete_the_first_support_case()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var port = AvailablePort();
        var baseUrl = $"http://localhost:{port}";
        await using var smtp = new CapturingSmtpServer();
        var api = StartApi(root, port);
        var standardOutput = api.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = api.StandardError.ReadToEndAsync(cancellationToken);
        var cliDirectory = Path.Combine(Path.GetTempPath(), $"helpaffe-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cliDirectory);
        var cliPath = Path.Combine(cliDirectory, OperatingSystem.IsWindows() ? "helpaffe.exe" : "helpaffe");

        try
        {
            await WaitUntilReady(baseUrl, api, cancellationToken);
            var setup = await ProvisionAgent(baseUrl, smtp.Port, cancellationToken);
            using var productTransport = new HttpClient();
            var address = new Uri(baseUrl);
            var firstProduct = new HelpaffeProductClient(productTransport, address, setup.FirstProductToken);
            var secondProduct = new HelpaffeProductClient(productTransport, address, setup.SecondProductToken);
            const string externalUserId = "shared-product-user";
            var source = await firstProduct.CreateTicketAsync(new CreateProductTicket(
                    externalUserId,
                    "Shared Customer",
                    "customer@example.test",
                    "Primary SDK workflow",
                    "Please prove the whole workflow.",
                    JsonSerializer.SerializeToElement(new { release = "2.4.1", page = "/settings" })),
                "sdk-create-primary", cancellationToken);
            await Task.Delay(10, cancellationToken);
            var relatedTicket = await firstProduct.CreateTicketAsync(new CreateProductTicket(
                    externalUserId,
                    "Shared Customer",
                    "customer@example.test",
                    "Previous SDK workflow",
                    "This belongs to the first product too."),
                "sdk-create-related", cancellationToken);
            var hiddenTicket = await secondProduct.CreateTicketAsync(new CreateProductTicket(
                    externalUserId,
                    "Shared Customer",
                    "customer@example.test",
                    "Hidden SDK workflow",
                    "This belongs to a different product."),
                "sdk-create-hidden", cancellationToken);
            await BuildCli(root, cliPath, cancellationToken);

            var createdSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "create", setup.FirstProjectId.ToString(), "--key", "postgres-restart", "--title", "Restart PostgreSQL safely", "--markdown-file", "-"],
                "Use the tested PostgreSQL restart playbook.\r\n", cancellationToken);
            Assert.Equal(0, createdSolution.ExitCode);
            using var createdSolutionJson = JsonDocument.Parse(createdSolution.StandardOutput);
            Assert.Equal("postgres-restart", createdSolutionJson.RootElement.GetProperty("key").GetString());
            Assert.Equal(1, createdSolutionJson.RootElement.GetProperty("version").GetInt32());

            var hiddenSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "create", setup.SecondProjectId.ToString(), "--key", "hidden", "--title", "Hidden", "--markdown", "Must not persist."],
                null, cancellationToken);
            Assert.Equal(3, hiddenSolution.ExitCode);
            Assert.Empty(hiddenSolution.StandardOutput);

            var searchedSolutions = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "list", setup.FirstProjectId.ToString(), "--search", "PostgreSQL"],
                null, cancellationToken);
            Assert.Equal(0, searchedSolutions.ExitCode);
            using var searchedSolutionsJson = JsonDocument.Parse(searchedSolutions.StandardOutput);
            Assert.Single(searchedSolutionsJson.RootElement.GetProperty("items").EnumerateArray());

            var readSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "get", setup.FirstProjectId.ToString(), "postgres-restart"],
                null, cancellationToken);
            Assert.Equal(0, readSolution.ExitCode);
            using var readSolutionJson = JsonDocument.Parse(readSolution.StandardOutput);
            Assert.Equal("Use the tested PostgreSQL restart playbook.\n",
                readSolutionJson.RootElement.GetProperty("markdown").GetString());

            var updatedSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "update", setup.FirstProjectId.ToString(), "postgres-restart", "--version", "1", "--title", "Restart PostgreSQL", "--markdown-file", "-"],
                "Use the current runbook.\n", cancellationToken);
            Assert.Equal(0, updatedSolution.ExitCode);
            using var updatedSolutionJson = JsonDocument.Parse(updatedSolution.StandardOutput);
            Assert.Equal(2, updatedSolutionJson.RootElement.GetProperty("version").GetInt32());

            var staleSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "update", setup.FirstProjectId.ToString(), "postgres-restart", "--version", "1", "--title", "Stale", "--markdown", "Must not win."],
                null, cancellationToken);
            Assert.Equal(6, staleSolution.ExitCode);
            Assert.Empty(staleSolution.StandardOutput);
            using var staleSolutionJson = JsonDocument.Parse(staleSolution.StandardError);
            Assert.Equal(2, staleSolutionJson.RootElement.GetProperty("current_version").GetInt32());

            var deletedSolution = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "solution", "delete", setup.FirstProjectId.ToString(), "postgres-restart", "--version", "2"],
                null, cancellationToken);
            Assert.Equal(0, deletedSolution.ExitCode);
            using var deletedSolutionJson = JsonDocument.Parse(deletedSolution.StandardOutput);
            Assert.True(deletedSolutionJson.RootElement.GetProperty("deleted").GetBoolean());

            var searched = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "list", "--project", setup.FirstProjectId.ToString(), "--status", "open", "--search", "workflow"],
                null, cancellationToken);
            Assert.Equal(0, searched.ExitCode);
            using var searchedJson = JsonDocument.Parse(searched.StandardOutput);
            var searchedItems = searchedJson.RootElement.GetProperty("items");
            Assert.Equal(2, searchedItems.GetArrayLength());
            Assert.DoesNotContain(searchedItems.EnumerateArray(),
                value => value.GetProperty("number").GetString() == hiddenTicket.Summary.Number);

            var context = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "get", source.Summary.Number], null, cancellationToken);
            Assert.Equal(0, context.ExitCode);
            using var contextJson = JsonDocument.Parse(context.StandardOutput);
            Assert.Equal("2.4.1", contextJson.RootElement.GetProperty("context").GetProperty("release").GetString());

            var related = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "requester-tickets", source.Summary.Number], null, cancellationToken);
            Assert.Equal(0, related.ExitCode);
            using var relatedJson = JsonDocument.Parse(related.StandardOutput);
            var relatedItems = relatedJson.RootElement.GetProperty("items");
            Assert.Single(relatedItems.EnumerateArray());
            Assert.Equal(relatedTicket.Summary.Number, relatedItems[0].GetProperty("number").GetString());

            var hidden = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "get", hiddenTicket.Summary.Number], null, cancellationToken);
            Assert.Equal(3, hidden.ExitCode);
            Assert.Empty(hidden.StandardOutput);

            var acquired = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "next", "--project", setup.FirstProjectId.ToString()], null, cancellationToken);
            Assert.Equal(0, acquired.ExitCode);
            using var acquiredJson = JsonDocument.Parse(acquired.StandardOutput);
            var acquiredSummary = acquiredJson.RootElement.GetProperty("summary");
            Assert.Equal(source.Summary.Number, acquiredSummary.GetProperty("number").GetString());
            var acquiredVersion = acquiredSummary.GetProperty("version").GetInt32();

            var noted = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "note", source.Summary.Number, "--version", acquiredVersion.ToString(), "--note-file", "-"],
                "Investigated through stdin.\r\n", cancellationToken);
            Assert.Equal(0, noted.ExitCode);
            using var notedJson = JsonDocument.Parse(noted.StandardOutput);
            var notedVersion = notedJson.RootElement.GetProperty("summary").GetProperty("version").GetInt32();

            var waiting = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "reply", source.Summary.Number, "--version", notedVersion.ToString(), "--status", "waiting_for_customer", "--message-file", "-"],
                "Please send one more detail.\n", cancellationToken);
            Assert.Equal(0, waiting.ExitCode);
            using var waitingJson = JsonDocument.Parse(waiting.StandardOutput);
            var waitingSummary = waitingJson.RootElement.GetProperty("summary");
            Assert.Equal("waiting_for_customer", waitingSummary.GetProperty("status").GetString());
            var waitingVersion = waitingSummary.GetProperty("version").GetInt32();

            var customerReply = await firstProduct.AddReplyAsync(
                source.Summary.Number, externalUserId, "Here is the missing detail.", waitingVersion,
                "sdk-customer-reply", cancellationToken);
            Assert.Equal(ProductTicketStatus.Open, customerReply.Summary.Status);
            var customerReplay = await firstProduct.AddReplyAsync(
                source.Summary.Number, externalUserId, "Here is the missing detail.", waitingVersion,
                "sdk-customer-reply", cancellationToken);
            Assert.Equal(customerReply.Summary.Version, customerReplay.Summary.Version);
            Assert.Equal(customerReply.Conversation.Count, customerReplay.Conversation.Count);

            var resolved = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "reply", source.Summary.Number, "--version", customerReply.Summary.Version.ToString(), "--status", "resolved", "--message-file", "-"],
                "This is resolved.\n", cancellationToken);
            Assert.Equal(0, resolved.ExitCode);
            using var resolvedJson = JsonDocument.Parse(resolved.StandardOutput);
            Assert.Equal("resolved", resolvedJson.RootElement.GetProperty("summary").GetProperty("status").GetString());

            var stale = await RunCli(cliPath, baseUrl, setup.AgentToken,
                ["--json", "ticket", "reopen", source.Summary.Number, "--version", acquiredVersion.ToString()], null, cancellationToken);
            Assert.Equal(6, stale.ExitCode);
            Assert.Empty(stale.StandardOutput);
            using var staleJson = JsonDocument.Parse(stale.StandardError);
            Assert.Equal("/problems/stale", staleJson.RootElement.GetProperty("type").GetString());
            Assert.True(staleJson.RootElement.GetProperty("current_version").GetInt32() > acquiredVersion);

            var webDetail = await WaitForSubmittedNotifications(
                baseUrl, setup.AgentToken, source.Summary.Number, expectedCount: 5, cancellationToken);
            Assert.Equal("Restricted CLI support",
                webDetail.GetProperty("summary").GetProperty("assignee").GetProperty("name").GetString());
            Assert.Equal("2.4.1", webDetail.GetProperty("context").GetProperty("release").GetString());
            Assert.Contains(webDetail.GetProperty("conversation").EnumerateArray(), value =>
                value.GetProperty("body").GetString() == "Here is the missing detail.");
            Assert.Contains(webDetail.GetProperty("conversation").EnumerateArray(), value =>
                value.GetProperty("body").GetString() == "This is resolved.");
            Assert.All(webDetail.GetProperty("notifications").EnumerateArray(),
                value => Assert.Equal("submitted_to_smtp", value.GetProperty("status").GetString()));
            var webHistory = await ReadBackoffice(baseUrl, setup.AgentToken,
                $"/api/backoffice/tickets/{source.Summary.Number}/requester-tickets", cancellationToken);
            Assert.Single(webHistory.GetProperty("items").EnumerateArray());
            Assert.Equal(relatedTicket.Summary.Number,
                webHistory.GetProperty("items")[0].GetProperty("number").GetString());

            var recipients = new List<string>();
            for (var index = 0; index < 9; index++)
                recipients.Add(Recipient(await smtp.ReadMessageAsync(cancellationToken)));
            Assert.Equal(5, recipients.Count(value => value == "customer@example.test"));
            Assert.Equal(2, recipients.Count(value => value == "first-support@example.test"));
            Assert.Single(recipients, value => value == "second-support@example.test");
            Assert.Single(recipients, value => value == "restricted-cli@example.test");

            await using var database = CreateDatabase();
            var ticket = await database.Tickets.Include(value => value.Conversation)
                .SingleAsync(value => value.Number == source.Summary.Number, cancellationToken);
            Assert.Equal(TicketStatus.Resolved, ticket.Status);
            Assert.Contains(ticket.Conversation, value =>
                value.Kind is ConversationEntryKind.InternalNote && value.Body == "Investigated through stdin.");
            Assert.Contains(ticket.Conversation, value =>
                value.Kind is ConversationEntryKind.PublicReply && value.Body == "This is resolved.");
            Assert.Single(ticket.Conversation, value =>
                value.Kind is ConversationEntryKind.CustomerMessage && value.Body == "Here is the missing detail.");
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
        start.Environment["Secrets__EncryptionKey"] = EncryptionKey;
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

    private static async Task<AcceptanceSetup> ProvisionAgent(
        string baseUrl,
        int smtpPort,
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

        await ConfigureEmail(client, projectId, smtpPort, "First product", "first-support@example.test", cancellationToken);
        await ConfigureEmail(client, hiddenProjectId, smtpPort, "Second product", "second-support@example.test", cancellationToken);

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
        var firstProductToken = await CreateProductKey(client, projectId, "First product acceptance", cancellationToken);
        var secondProductToken = await CreateProductKey(client, hiddenProjectId, "Second product acceptance", cancellationToken);
        return new AcceptanceSetup(
            projectId,
            hiddenProjectId,
            agent.GetProperty("token").GetString()!,
            firstProductToken,
            secondProductToken);
    }

    private static async Task ConfigureEmail(
        HttpClient client,
        Guid projectId,
        int smtpPort,
        string brand,
        string supportRecipient,
        CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync($"/api/backoffice/projects/{projectId}/email-settings", new
        {
            language = "en",
            smtp = new { host = "127.0.0.1", port = smtpPort, use_tls = false, username = (string?)null, password = (string?)null },
            sender = new { name = brand, email = $"sender-{projectId:N}@example.test" },
            support_recipients = new[] { supportRecipient },
            branding = new { name = brand, logo_url = (string?)null, color = "#336699" },
            ticket_links = new
            {
                customer = "https://product.example.test/support/{{ticket_number}}",
                backoffice = "https://support.example.test/tickets/{{ticket_number}}",
            },
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var templates = await client.GetAsync(
            $"/api/backoffice/projects/{projectId}/email-templates", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, templates.StatusCode);
        var templateBody = await templates.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal(5, templateBody.GetArrayLength());
    }

    private static async Task<string> CreateProductKey(
        HttpClient client,
        Guid projectId,
        string name,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/backoffice/projects/{projectId}/product-keys", new { name }, cancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("token").GetString()!;
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

    private static async Task<JsonElement> WaitForSubmittedNotifications(
        string baseUrl,
        string token,
        string number,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var detail = await ReadBackoffice(
                baseUrl, token, $"/api/backoffice/tickets/{number}", cancellationToken);
            var notifications = detail.GetProperty("notifications");
            if (notifications.GetArrayLength() == expectedCount && notifications.EnumerateArray().All(value =>
                    value.GetProperty("status").GetString() == "submitted_to_smtp"))
                return detail;
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("Ticket notifications were not submitted to SMTP.");
    }

    private static async Task<JsonElement> ReadBackoffice(
        string baseUrl,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    private static string Recipient(string message) => message.Split('\n')
        .Single(line => line.StartsWith("To: ", StringComparison.Ordinal))[4..].Trim();

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

    private sealed record AcceptanceSetup(
        Guid FirstProjectId,
        Guid SecondProjectId,
        string AgentToken,
        string FirstProductToken,
        string SecondProductToken);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class CapturingSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();
        private readonly Task _serve;

        public CapturingSmtpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = ServeAsync();
        }

        public int Port { get; }

        public Task<string> ReadMessageAsync(CancellationToken cancellationToken) =>
            _messages.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        private async Task ServeAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                    await HandleAsync(client);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            })
            {
                await writer.WriteLineAsync("220 localhost helpaffe acceptance SMTP");
                while (await reader.ReadLineAsync(_cancellation.Token) is { } line)
                {
                    if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                        var message = new StringBuilder();
                        while (await reader.ReadLineAsync(_cancellation.Token) is { } data && data != ".")
                            message.AppendLine(data.StartsWith("..", StringComparison.Ordinal) ? data[1..] : data);
                        await _messages.Writer.WriteAsync(message.ToString(), _cancellation.Token);
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 ok");
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _cancellation.Dispose();
        }
    }
}
