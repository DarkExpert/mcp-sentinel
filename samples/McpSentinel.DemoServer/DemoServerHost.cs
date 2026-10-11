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
            .WithTools(tools);
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }
}
