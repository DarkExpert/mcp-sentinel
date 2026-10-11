using McpSentinel.DemoServer;

await using var app = DemoServerHost.Build(WebApplication.CreateBuilder(args));
await app.RunAsync();
