using McpSentinel.Core;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpSentinel.Gateway;

public static class GatewayHost
{
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    public static async Task<WebApplication> BuildAsync(WebApplicationBuilder builder, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://localhost:5094");
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);

        var endpointValue = builder.Configuration["Gateway:UpstreamUrl"];
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "http" && endpoint.Scheme != "https") || !endpoint.IsLoopback ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Gateway:UpstreamUrl must be an absolute loopback HTTP(S) URL without credentials, query or fragment.");

        var policyPath = builder.Configuration["Gateway:PolicyPath"] ?? Path.Combine(AppContext.BaseDirectory, "policies", "demo-policy.json");
        var engine = new PolicyEngine(PolicyConfigurationJson.Parse(await File.ReadAllTextAsync(policyPath, cancellationToken)));

        // Never follow redirects or system proxies out of the trusted local upstream boundary.
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = OperationTimeout
        };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = OperationTimeout
        }, httpClient, ownsHttpClient: true);
        McpClient? upstream = null;
        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(OperationTimeout);
            upstream = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                InitializationTimeout = OperationTimeout
            }, cancellationToken: startupTimeout.Token);
            var discovered = await upstream.ListToolsAsync(cancellationToken: startupTimeout.Token);
            var tools = discovered.Select(tool => tool.ProtocolTool).ToArray();
            var toolNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name) || !toolNames.Add(tool.Name))
                    throw new InvalidOperationException("Upstream advertised invalid or duplicate tool names.");
            }

            var connection = upstream;
            // Factory registrations transfer async disposal ownership to the host's service provider.
            builder.Services.AddSingleton(_ => transport);
            builder.Services.AddSingleton(_ => connection);
            builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                .WithListToolsHandler((_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new ListToolsResult { Tools = tools.ToList() });
                })
                .WithCallToolHandler(async (request, token) =>
                {
                    var parameters = request.Params;
                    var decision = engine.Evaluate(parameters?.Name);
                    var audit = SecurityAudit.Create(request.Services!, parameters?.Name, decision.Action);
                    audit.RecordDecision();
                    var enforcement = "NotForwarded";
                    var upstreamStatus = "NotAttempted";
                    string? errorCategory = null;
                    CancellationToken operationToken = default;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (decision.Action != PolicyAction.Allow)
                        {
                            enforcement = "Blocked";
                            errorCategory = decision.Action == PolicyAction.RequireApproval ? "ApprovalUnavailable" : "PolicyDenied";
                            return Error(decision.Action == PolicyAction.RequireApproval
                                ? "Approval required. Execution blocked; approval workflow is unavailable."
                                : "Tool execution denied by policy.");
                        }
                        if (parameters is null || !toolNames.Contains(parameters.Name))
                        {
                            enforcement = "Blocked";
                            errorCategory = "UnknownUpstreamTool";
                            return Error("Tool is not in the validated upstream catalog.");
                        }

                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(OperationTimeout);
                        operationToken = timeout.Token;
                        enforcement = "Forwarded";
                        upstreamStatus = "Unknown";
                        // SDK continuation may dispatch multiple upstream requests within this operation.
                        var result = await connection.CallToolAsync(new CallToolRequestParams
                        {
                            Name = parameters.Name,
                            Arguments = parameters.Arguments
                        }, timeout.Token);
                        var adapted = AdaptResult(result, request.Server.NegotiatedProtocolVersion);
                        upstreamStatus = result.IsError == true ? "ToolError" : "Succeeded";
                        errorCategory = result.IsError == true ? "UpstreamToolError" : null;
                        return adapted;
                    }
                    catch (OperationCanceledException)
                    {
                        errorCategory = !token.IsCancellationRequested && operationToken.IsCancellationRequested ? "Timeout" : "Canceled";
                        throw;
                    }
                    catch (Exception exception)
                    {
                        errorCategory = exception switch
                        {
                            HttpRequestException => "TransportError",
                            McpProtocolException => "ProtocolError",
                            McpException => "SdkError",
                            _ => "InternalError"
                        };
                        throw;
                    }
                    finally
                    {
                        audit.RecordCompletion(enforcement, upstreamStatus, errorCategory);
                    }
                });
            var app = builder.Build();
            _ = app.Services.GetRequiredService<HttpClientTransport>();
            _ = app.Services.GetRequiredService<McpClient>();
            app.MapMcp("/mcp");
            return app;
        }
        catch
        {
            if (upstream is not null)
                await upstream.DisposeAsync();
            await transport.DisposeAsync();
            throw;
        }
    }

    private static CallToolResult AdaptResult(CallToolResult upstream, string? downstreamVersion)
    {
        // Only completed tool results are supported. Never reinterpret an alternate result as success.
        if (upstream.ResultType is not null && upstream.ResultType != "complete")
            throw new McpException("Unsupported upstream tool result type.");

        var result = new CallToolResult
        {
            Content = upstream.Content.ToList(),
            StructuredContent = upstream.StructuredContent?.Clone(),
            IsError = upstream.IsError,
            Meta = upstream.Meta?.DeepClone().AsObject()
            // ResultType deliberately remains null on this new object. SDK 2.2.0's
            // SetWithAlternateHandler stamps "complete" only for 2026-07-28+ requests.
        };
        if (downstreamVersion is "2024-11-05" or "2025-03-26" && result.StructuredContent is { } structured)
        {
            // These revisions predate structuredContent. Preserve its data in compatible text content.
            result.Content.Add(new TextContentBlock { Text = structured.GetRawText() });
            result.StructuredContent = null;
        }
        return result;
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
