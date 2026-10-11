using McpSentinel.Gateway;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
await using var app = await GatewayHost.BuildAsync(builder);
if (builder.Configuration.GetValue<bool>("Demo:Controlled"))
{
    // Private process-control channel for scripts/run-demo.ps1, never an HTTP endpoint.
    await app.StartAsync();
    Console.WriteLine("MCP_SENTINEL_READY");
    await Console.In.ReadLineAsync();
    await app.StopAsync();
}
else await app.RunAsync();
