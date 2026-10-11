using ModelContextProtocol.AspNetCore;

namespace McpSentinel.DemoServer;

public static class DemoServerHost
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
            builder.WebHost.UseUrls("http://localhost:5119");
        // Protocol diagnostics can contain arguments/results. Disable SDK logging even in development.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
        var tools = new DemoTools();
        builder.Services.AddSingleton(tools);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(tools)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
            {
                var result = await next(request, token);
                var name = request.Params?.Name;
                if (name is not null && tools.InvocationCount(name) > 0)
                    request.Services!.GetRequiredService<ILogger<DemoTools>>().LogInformation(
                        new EventId(2000, "DemoInvocationCount"), "DemoInvocationCount ToolName={ToolName} InvocationCount={InvocationCount}",
                        name, tools.InvocationCount(name));
                return result;
            }));
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }
}
