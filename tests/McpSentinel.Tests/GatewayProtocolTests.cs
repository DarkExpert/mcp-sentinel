using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using ModelContextProtocol.Server;

namespace McpSentinel.Tests;

public sealed class GatewayProtocolTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuditFailuresPreserveSuccessfulToolResults(bool failDecision, bool failCompletion)
    {
        var provider = new FaultingAuditProvider(failDecision, failCompletion);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = false, Content = [new TextContentBlock { Text = "PRIVATE_RESPONSE_CONTENT" }],
                StructuredContent = JsonSerializer.SerializeToElement(new { value = 42 }),
                Meta = new JsonObject { ["preserved"] = true }
            });
        }, provider);
        var result = await hosts.Client.CallToolAsync("customer.read", new Dictionary<string, object?>
        {
            ["credential"] = "PRIVATE_ARGUMENT"
        }, cancellationToken: hosts.Token);
        Assert.False(result.IsError == true);
        Assert.Equal("PRIVATE_RESPONSE_CONTENT", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(42, result.StructuredContent!.Value.GetProperty("value").GetInt32());
        Assert.True(result.Meta!["preserved"]!.GetValue<bool>());
        Assert.Equal(1, invocations);
        AssertAuditAttemptsAndRedaction(hosts, provider);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuditFailuresPreserveOriginalUpstreamProtocolException(bool failDecision, bool failCompletion)
    {
        // Compare the real two-hop SDK error with a healthy-audit baseline; each hop
        // decorates remote exception messages, so raw upstream text is not the wire message.
        await using var baselineHosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
            throw new McpProtocolException("Original upstream failure", McpErrorCode.InvalidParams));
        var baseline = await Assert.ThrowsAsync<McpProtocolException>(() => baselineHosts.Client.CallToolAsync(
            "customer.read", cancellationToken: baselineHosts.Token).AsTask());
        Assert.Contains("Original upstream failure", baseline.Message);
        var provider = new FaultingAuditProvider(failDecision, failCompletion);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new McpProtocolException("Original upstream failure", McpErrorCode.InvalidParams);
        }, provider);
        var exception = await Assert.ThrowsAsync<McpProtocolException>(() => hosts.Client.CallToolAsync(
            "customer.read", new Dictionary<string, object?> { ["credential"] = "PRIVATE_ARGUMENT" }, cancellationToken: hosts.Token).AsTask());
        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Equal(baseline.Message, exception.Message);
        Assert.DoesNotContain(FaultingAuditProvider.Secret, exception.ToString());
        Assert.Equal(1, invocations);
        AssertAuditAttemptsAndRedaction(hosts, provider);
    }

    [Theory]
    [InlineData("customer.delete", "denied by policy")]
    [InlineData("invoice.refund", "Approval required")]
    [InlineData("unknown", "denied by policy")]
    public async Task AuditFailuresNeverForwardBlockedTools(string tool, string expectedMessage)
    {
        var provider = new FaultingAuditProvider(true, true);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(new CallToolResult());
        }, provider);
        var result = await hosts.Client.CallToolAsync(tool, new Dictionary<string, object?> { ["credential"] = "PRIVATE_ARGUMENT" }, cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Contains(expectedMessage, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, invocations);
        AssertAuditAttemptsAndRedaction(hosts, provider);
    }

    [Fact]
    public async Task AuditFailuresPreserveToolErrorResults()
    {
        var provider = new FaultingAuditProvider(true, true);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true, Content = [new TextContentBlock { Text = "PRIVATE_RESPONSE_CONTENT" }]
            });
        }, provider);
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal("PRIVATE_RESPONSE_CONTENT", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(1, invocations);
        AssertAuditAttemptsAndRedaction(hosts, provider);
    }

    [Fact]
    public async Task AuditFailuresDoNotSuppressCancellation()
    {
        var provider = new FaultingAuditProvider(true, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", async (_, token) =>
        {
            Interlocked.Increment(ref invocations);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled upstream must not complete.");
        }, provider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(hosts.Token);
        var call = hosts.Client.CallToolAsync("customer.read", cancellationToken: cancellation.Token).AsTask();
        await started.Task.WaitAsync(hosts.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await hosts.Audit.Completion.WaitAsync(hosts.Token);
        await provider.CompletionAttempt.WaitAsync(hosts.Token);
        Assert.Equal(1, invocations);
        AssertAuditAttemptsAndRedaction(hosts, provider);
    }

    [Fact]
    public async Task AuditLoggerResolutionFailureDoesNotAbortExecution()
    {
        var provider = new FaultingAuditProvider(false, false, failResolution: true);
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(new CallToolResult { IsError = false });
        }, provider);
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.False(result.IsError == true);
        Assert.Equal(1, invocations);
        Assert.Equal(1, provider.ResolutionAttempts);
        Assert.DoesNotContain(FaultingAuditProvider.Secret, JsonSerializer.Serialize(hosts.Capture.Responses));
    }

    private static void AssertAuditAttemptsAndRedaction(ProtocolHosts hosts, FaultingAuditProvider provider)
    {
        Assert.Equal(1, hosts.DownstreamHandlerEntries);
        Assert.Equal(1, provider.DecisionAttempts);
        Assert.Equal(1, provider.CompletionAttempts);
        Assert.Single(hosts.Audit.Entries, entry => entry.EventId == 1000);
        Assert.Single(hosts.Audit.Entries, entry => entry.EventId == 1001);
        var auditJson = JsonSerializer.Serialize(hosts.Audit.Entries);
        foreach (var secret in new[] { FaultingAuditProvider.Secret, "PRIVATE_ARGUMENT", "PRIVATE_RESPONSE_CONTENT" })
            Assert.DoesNotContain(secret, auditJson);
        var wire = JsonSerializer.Serialize(hosts.Capture.Responses);
        Assert.DoesNotContain(FaultingAuditProvider.Secret, wire);
        Assert.DoesNotContain("PRIVATE_ARGUMENT", wire);
        Assert.DoesNotContain("StackTrace", wire);
    }

    [Theory]
    [InlineData("2024-11-05", false)]
    [InlineData("2024-11-05", true)]
    [InlineData("2025-03-26", false)]
    [InlineData("2025-03-26", true)]
    [InlineData("2025-06-18", false)]
    [InlineData("2025-06-18", true)]
    [InlineData("2025-11-25", false)]
    [InlineData("2025-11-25", true)]
    [InlineData("2026-07-28", false)]
    [InlineData("2026-07-28", true)]
    public async Task WireResultsMatchDownstreamRevision(string version, bool isError)
    {
        var invocations = 0;
        string? upstreamVersion = null;
        await using var hosts = await ProtocolHosts.StartAsync(version, (request, _) =>
        {
            Interlocked.Increment(ref invocations);
            upstreamVersion = request.Server.NegotiatedProtocolVersion;
            return ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Mock result" }],
                StructuredContent = JsonSerializer.SerializeToElement(new { customerId = "customer-1" }),
                IsError = isError,
                Meta = new JsonObject { ["demo"] = "preserved" }
            });
        });
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.Equal(isError, result.IsError);
        Assert.Equal("2026-07-28", upstreamVersion);
        Assert.Equal(version, hosts.Client.NegotiatedProtocolVersion);
        Assert.Equal(1, invocations);

        // This is the actual HTTP response body consumed by the MCP client, not reserialized DTOs.
        var wire = Assert.Single(hosts.Capture.Responses, response =>
            response.TryGetProperty("result", out var value) && value.TryGetProperty("content", out _))
            .GetProperty("result");
        Assert.Equal(isError, wire.GetProperty("isError").GetBoolean());
        Assert.Equal("Mock result", wire.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("preserved", wire.GetProperty("_meta").GetProperty("demo").GetString());
        if (version == "2026-07-28")
            Assert.Equal("complete", wire.GetProperty("resultType").GetString());
        else
            Assert.False(wire.TryGetProperty("resultType", out _));

        if (version is "2024-11-05" or "2025-03-26")
        {
            Assert.False(wire.TryGetProperty("structuredContent", out _));
            using var fallback = JsonDocument.Parse(wire.GetProperty("content")[1].GetProperty("text").GetString()!);
            Assert.Equal("customer-1", fallback.RootElement.GetProperty("customerId").GetString());
        }
        else
        {
            Assert.Equal("customer-1", wire.GetProperty("structuredContent").GetProperty("customerId").GetString());
        }
    }

    [Theory]
    [InlineData("customer.read", 2)]
    [InlineData("customer.delete", 0)]
    [InlineData("invoice.refund", 0)]
    [InlineData("unknown", 0)]
    public async Task SdkContinuationStaysInsideOneAllowedDownstreamInvocation(string tool, int expectedInvocations)
    {
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (request, _) =>
        {
            var count = Interlocked.Increment(ref invocations);
            Assert.Equal("2026-07-28", request.Server.NegotiatedProtocolVersion);
            if (count == 1)
            {
                Assert.Null(request.Params!.RequestState);
                throw new InputRequiredException(new InputRequiredResult { RequestState = "continue-once" });
            }
            Assert.Equal("continue-once", request.Params!.RequestState);
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = false, Content = [new TextContentBlock { Text = "Continuation complete" }]
            });
        });
        var result = await hosts.Client.CallToolAsync(tool, cancellationToken: hosts.Token);
        Assert.Equal(expectedInvocations, invocations);
        Assert.Equal(1, hosts.DownstreamHandlerEntries);
        Assert.Single(hosts.Audit.Entries, entry => entry.EventId == 1001);
        if (expectedInvocations == 0) Assert.True(result.IsError);
        else
        {
            Assert.False(result.IsError == true);
            Assert.Equal("Continuation complete", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }
    }

    [Fact]
    public async Task RepeatedContinuationsExhaustSdkLimitAndReturnError()
    {
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new InputRequiredException(new InputRequiredResult { RequestState = "repeat" });
        });
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal(11, invocations); // Initial dispatch plus SDK 2.2.0's ten continuation retries.
        Assert.Equal(1, hosts.DownstreamHandlerEntries);
        var audit = Assert.Single(hosts.Audit.Entries, entry => entry.EventId == 1001);
        Assert.Equal("Unknown", audit.Fields["UpstreamStatus"]);
        Assert.Equal("SdkError", audit.Fields["ErrorCategory"]);
    }

    [Fact]
    public async Task MalformedContinuationReturnsErrorWithoutRedispatch()
    {
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new InputRequiredException(new InputRequiredResult());
        });
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task UnsupportedResultDiscriminatorIsNotConvertedToSuccess()
    {
        var invocations = 0;
        await using var hosts = await ProtocolHosts.StartAsync("2025-06-18", (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(new CallToolResult
            {
                ResultType = "unsupported", IsError = false,
                Content = [new TextContentBlock { Text = "Must not become successful" }]
            });
        });
        var result = await hosts.Client.CallToolAsync("customer.read", cancellationToken: hosts.Token);
        Assert.True(result.IsError);
        Assert.Equal(1, invocations);
    }

    private static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder;
    }

    private sealed class ProtocolHosts : IAsyncDisposable
    {
        private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(30));
        private WebApplication? upstream;
        private WebApplication? gateway;
        private HttpClientTransport? transport;
        private int handlerEntries;
        public int DownstreamHandlerEntries => Volatile.Read(ref handlerEntries);
        public CancellationToken Token => budget.Token;
        public WireCapture Capture { get; } = new();
        public AuditLogCollector Audit { get; } = new();
        public McpClient Client { get; private set; } = null!;

        public static async Task<ProtocolHosts> StartAsync(string version, McpRequestHandler<CallToolRequestParams, CallToolResult> handler, ILoggerProvider? auditProvider = null)
        {
            var hosts = new ProtocolHosts();
            try
            {
                var builder = Builder();
                builder.Services.AddMcpServer()
                    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                    {
                        Tools = new[] { "customer.read", "customer.delete", "invoice.refund" }.Select(name => new Tool
                        {
                            Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                        }).ToList()
                    }))
                    .WithCallToolHandler(handler);
                hosts.upstream = builder.Build();
                hosts.upstream.MapMcp("/mcp");
                await hosts.upstream.StartAsync(hosts.Token);
                var gatewayBuilder = Builder();
                gatewayBuilder.Logging.AddProvider(hosts.Audit);
                if (auditProvider is not null) gatewayBuilder.Logging.AddProvider(auditProvider);
                gatewayBuilder.Configuration["Gateway:UpstreamUrl"] = Assert.Single(hosts.upstream.Urls) + "/mcp";
                gatewayBuilder.Configuration["Gateway:PolicyPath"] = Path.Combine(AppContext.BaseDirectory, "policies", "demo-policy.json");
                // Counts downstream entries without replacing Gateway policy or forwarding handlers.
                gatewayBuilder.Services.AddMcpServer().WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
                {
                    Interlocked.Increment(ref hosts.handlerEntries);
                    return await next(request, token);
                }));
                hosts.gateway = await GatewayHost.BuildAsync(gatewayBuilder, hosts.Token);
                await hosts.gateway.StartAsync(hosts.Token);
                var httpClient = new HttpClient(hosts.Capture);
                hosts.transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(Assert.Single(hosts.gateway.Urls) + "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = TimeSpan.FromSeconds(10)
                }, httpClient, ownsHttpClient: true);
                hosts.Client = await McpClient.CreateAsync(hosts.transport, new McpClientOptions
                {
                    ProtocolVersion = version, InitializationTimeout = TimeSpan.FromSeconds(10)
                }, cancellationToken: hosts.Token);
                return hosts;
            }
            catch { await hosts.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null) await Client.DisposeAsync();
            if (transport is not null) await transport.DisposeAsync();
            if (gateway is not null) { await gateway.StopAsync(); await gateway.DisposeAsync(); }
            if (upstream is not null) { await upstream.StopAsync(); await upstream.DisposeAsync(); }
            budget.Dispose();
        }
    }

    // Capture-only observer. Requests and responses are still processed by the official MCP SDK.
    private sealed class WireCapture : DelegatingHandler
    {
        public ConcurrentQueue<JsonElement> Responses { get; } = new();
        public WireCapture() : base(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            var body = await response.Content.ReadAsStringAsync(token); // Buffers content for the SDK's subsequent read.
            if (body.Length == 0) return response;
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
                await foreach (var item in SseParser.Create(stream).EnumerateAsync(token))
                    Capture(item.Data);
            }
            else if (response.Content.Headers.ContentType?.MediaType == "application/json") Capture(body);
            return response;
        }

        private void Capture(string json)
        {
            using var document = JsonDocument.Parse(json);
            Responses.Enqueue(document.RootElement.Clone());
        }
    }
}
