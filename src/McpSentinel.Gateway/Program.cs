using McpSentinel.Gateway;

await using var app = await GatewayHost.BuildAsync(WebApplication.CreateBuilder(args));
await app.RunAsync();
