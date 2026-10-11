using System.Text.Json;
using McpSentinel.DemoServer;
using McpSentinel.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpSentinel.Tests;

public sealed class GatewayIntegrationTests
{
    [Fact]
    public async Task InitializationAndDiscoveryPreserveUpstreamMetadata()
    {
        await using var hosts = await Hosts.StartAsync();
        Assert.NotNull(hosts.Client.ServerCapabilities.Tools);
        var tools = await hosts.Client.ListToolsAsync(cancellationToken: hosts.Token);
        Assert.Equal(["customer.delete", "customer.read", "invoice.refund"], tools.Select(t => t.Name).Order().ToArray());
        var read = Assert.Single(tools, tool => tool.Name == "customer.read");
        Assert.Equal("Read a mock customer by ID.", read.ProtocolTool.Description);
        Assert.True(read.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("customerId", out _));
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.read"));
    }

    [Fact]
    public async Task AllowedReadExecutesExactlyOnce()
    {
        await using var hosts = await Hosts.StartAsync();
        var result = await hosts.Client.CallToolAsync("customer.read", new Dictionary<string, object?>
        {
            ["customerId"] = "customer-1"
        }, cancellationToken: hosts.Token);
        Assert.False(result.IsError == true);
        Assert.Contains("Ada Example", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(1, hosts.Tools.InvocationCount("customer.read"));
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.delete"));
        Assert.Equal(0, hosts.Tools.InvocationCount("invoice.refund"));
    }

    [Theory]
    [InlineData("customer.delete", "customerId", "customer-1", "denied")]
    [InlineData("invoice.refund", "invoiceId", "invoice-1", "Approval required")]
    public async Task BlockedToolNeverExecutes(string tool, string argument, string value, string expectedMessage)
    {
        await using var hosts = await Hosts.StartAsync();
        // Invoke directly without discovery: tools/list is never an authorization prerequisite.
        var result = await hosts.Client.CallToolAsync(tool, new Dictionary<string, object?> { [argument] = value }, cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Contains(expectedMessage, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, hosts.Tools.InvocationCount(tool));
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.read"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Customer.read")]
    [InlineData("customer.read ")]
    public async Task UnknownOrNonExactToolNeverExecutes(string tool)
    {
        await using var hosts = await Hosts.StartAsync();
        var result = await hosts.Client.CallToolAsync(tool, cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.read"));
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.delete"));
        Assert.Equal(0, hosts.Tools.InvocationCount("invoice.refund"));
    }

    [Fact]
    public async Task UpstreamToolErrorRemainsAnError()
    {
        await using var hosts = await Hosts.StartAsync();
        var result = await hosts.Client.CallToolAsync("customer.read", new Dictionary<string, object?>
        {
            ["customerId"] = "missing-customer"
        }, cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal("Customer not found.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(1, hosts.Tools.InvocationCount("customer.read"));
    }

    [Fact]
    public async Task LosingUpstreamAfterStartupCannotProduceSuccess()
    {
        await using var hosts = await Hosts.StartAsync();
        await hosts.Demo.StopAsync(hosts.Token);
        var exception = await Record.ExceptionAsync(async () =>
        {
            var result = await hosts.Client.CallToolAsync("customer.read", new Dictionary<string, object?>
            {
                ["customerId"] = "customer-1"
            }, cancellationToken: hosts.Token);
            Assert.True(result.IsError);
        });
        if (exception is not null)
            Assert.IsAssignableFrom<McpException>(exception);
        Assert.Equal(0, hosts.Tools.InvocationCount("customer.read"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/mcp")]
    [InlineData("ftp://localhost/mcp")]
    [InlineData("http://example.com/mcp")]
    [InlineData("http://user:password@localhost/mcp")]
    [InlineData("http://localhost/mcp?secret=value")]
    [InlineData("http://localhost/mcp#fragment")]
    public async Task InvalidOrMissingEndpointPreventsHostCreation(string? endpoint)
    {
        var builder = CreateBuilder();
        builder.Configuration["Gateway:UpstreamUrl"] = endpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GatewayHost.BuildAsync(builder));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"defaultAction\":\"Allow\",\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"customer.read\",\"action\":\"Execute\"}]}")]
    public async Task InvalidPolicyPreventsHostCreation(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var builder = GatewayBuilder("http://127.0.0.1:1/mcp");
            builder.Configuration["Gateway:PolicyPath"] = path;
            await Assert.ThrowsAnyAsync<JsonException>(() => GatewayHost.BuildAsync(builder));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingPolicyPreventsHostCreation()
    {
        var builder = GatewayBuilder("http://127.0.0.1:1/mcp");
        builder.Configuration["Gateway:PolicyPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => GatewayHost.BuildAsync(builder));
    }

    [Fact]
    public async Task UnreadablePolicyPreventsHostCreation()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var builder = GatewayBuilder("http://127.0.0.1:1/mcp");
            builder.Configuration["Gateway:PolicyPath"] = path;
            await Assert.ThrowsAnyAsync<IOException>(() => GatewayHost.BuildAsync(builder));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UnavailableUpstreamPreventsHostCreation()
    {
        // A real local HTTP listener returns 503; no external service or port-allocation race.
        await using var unavailable = CreateBuilder().Build();
        unavailable.Map("/mcp", () => Microsoft.AspNetCore.Http.Results.StatusCode(503));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await unavailable.StartAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GatewayHost.BuildAsync(GatewayBuilder(Endpoint(unavailable)), timeout.Token));
        await unavailable.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task AllowedNameAbsentFromCatalogIsStillBlocked()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"unknown\",\"action\":\"Allow\"}]}");
            await using var hosts = await Hosts.StartAsync(path);
            var result = await hosts.Client.CallToolAsync("unknown", cancellationToken: hosts.Token);
            Assert.True(result.IsError);
            Assert.Contains("catalog", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.Equal(0, hosts.Tools.InvocationCount("customer.read"));
            Assert.Equal(0, hosts.Tools.InvocationCount("customer.delete"));
            Assert.Equal(0, hosts.Tools.InvocationCount("invoice.refund"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveRequestCancellationAndTimeoutReachUpstream(bool useTimeout)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        var builder = CreateBuilder();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool
                {
                    Name = "customer.read",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                }]
            }))
            .WithCallToolHandler(async (_, token) =>
            {
                Interlocked.Increment(ref invocations);
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                throw new InvalidOperationException("The controlled upstream must never complete successfully.");
            });
        await using var upstream = builder.Build();
        upstream.MapMcp("/mcp");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await upstream.StartAsync(budget.Token);
        await using var gateway = await GatewayHost.BuildAsync(GatewayBuilder(Endpoint(upstream)), budget.Token);
        await gateway.StartAsync(budget.Token);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Endpoint(gateway)), TransportMode = HttpTransportMode.StreamableHttp
        });
        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = "2025-06-18", InitializationTimeout = TimeSpan.FromSeconds(10)
        }, cancellationToken: budget.Token);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var call = client.CallToolAsync("customer.read", cancellationToken: requestCancellation.Token).AsTask();
        await started.Task.WaitAsync(budget.Token);
        if (!useTimeout) requestCancellation.Cancel();
        if (useTimeout)
        {
            var exception = await Record.ExceptionAsync(async () => Assert.True((await call).IsError));
            if (exception is not null) Assert.IsAssignableFrom<McpException>(exception);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        // Wait on a deterministic signal from the executing upstream handler, never a sleep.
        await canceled.Task.WaitAsync(budget.Token);
        Assert.Equal(1, Volatile.Read(ref invocations));
        await gateway.StopAsync(budget.Token);
        await upstream.StopAsync(budget.Token);
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private static WebApplicationBuilder GatewayBuilder(string endpoint)
    {
        var builder = CreateBuilder();
        builder.Configuration["Gateway:UpstreamUrl"] = endpoint;
        builder.Configuration["Gateway:PolicyPath"] = Path.Combine(AppContext.BaseDirectory, "policies", "demo-policy.json");
        return builder;
    }

    private static string Endpoint(WebApplication app) => Assert.Single(app.Urls) + "/mcp";

    private sealed class Hosts : IAsyncDisposable
    {
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        public CancellationToken Token => timeout.Token;
        public WebApplication Demo { get; private set; } = null!;
        private WebApplication? gateway;
        private HttpClientTransport? transport;
        public McpClient Client { get; private set; } = null!;
        public DemoTools Tools => Demo.Services.GetRequiredService<DemoTools>();

        public static async Task<Hosts> StartAsync(string? policyPath = null)
        {
            var hosts = new Hosts();
            try
            {
                hosts.Demo = DemoServerHost.Build(CreateBuilder());
                await hosts.Demo.StartAsync(hosts.Token);
                var gatewayBuilder = GatewayBuilder(Endpoint(hosts.Demo));
                if (policyPath is not null) gatewayBuilder.Configuration["Gateway:PolicyPath"] = policyPath;
                hosts.gateway = await GatewayHost.BuildAsync(gatewayBuilder, hosts.Token);
                await hosts.gateway.StartAsync(hosts.Token);
                hosts.transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(Endpoint(hosts.gateway)),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = TimeSpan.FromSeconds(10)
                });
                hosts.Client = await McpClient.CreateAsync(hosts.transport, new McpClientOptions
                {
                    InitializationTimeout = TimeSpan.FromSeconds(10),
                    // Exercise the standard initialize handshake, not just the newer discovery handshake.
                    ProtocolVersion = "2025-06-18"
                }, cancellationToken: hosts.Token);
                return hosts;
            }
            catch
            {
                await hosts.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (transport is not null) await transport.DisposeAsync();
            if (gateway is not null)
            {
                await gateway.StopAsync();
                await gateway.DisposeAsync();
            }
            if (Demo is not null)
            {
                await Demo.StopAsync();
                await Demo.DisposeAsync();
            }
            timeout.Dispose();
        }
    }
}
