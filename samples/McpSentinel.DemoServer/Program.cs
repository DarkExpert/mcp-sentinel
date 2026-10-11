using McpSentinel.DemoServer;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
await using var app = DemoServerHost.Build(builder);
if (builder.Configuration.GetValue<bool>("Demo:Controlled"))
{
    await app.StartAsync();
    Console.WriteLine("MCP_SENTINEL_READY");
    await Console.In.ReadLineAsync();
    await app.StopAsync();
}
else await app.RunAsync();
