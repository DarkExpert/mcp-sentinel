using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

try
{
    var endpoint = args.Length == 1 ? new Uri(args[0]) : new Uri("http://localhost:5094/mcp");
    if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0)
        throw new InvalidOperationException("The demo requires a local Gateway HTTP endpoint.");
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var httpClient = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    await using var transport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    }, httpClient);
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: budget.Token);
    var tools = await client.ListToolsAsync(cancellationToken: budget.Token);
    if (!tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "customer.delete", "customer.read", "invoice.refund" }))
        throw new InvalidOperationException("Unexpected tool catalog.");
    Console.WriteLine("Connected to Gateway via MCP Streamable HTTP.");
    Console.WriteLine("Discovered: customer.read, customer.delete, invoice.refund");
    var read = await client.CallToolAsync("customer.read", new Dictionary<string, object?> { ["customerId"] = "customer-1" }, cancellationToken: budget.Token);
    if (read.IsError == true || !read.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("Ada Example", StringComparison.Ordinal)))
        throw new InvalidOperationException("Read failed.");
    Console.WriteLine("customer.read   -> ALLOW            | upstream operation succeeded");
    var delete = await client.CallToolAsync("customer.delete", new Dictionary<string, object?> { ["customerId"] = "customer-1" }, cancellationToken: budget.Token);
    if (delete.IsError != true || !delete.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("denied by policy", StringComparison.Ordinal)))
        throw new InvalidOperationException("Delete was not blocked.");
    Console.WriteLine("customer.delete -> DENY             | blocked before upstream execution");
    var refund = await client.CallToolAsync("invoice.refund", new Dictionary<string, object?> { ["invoiceId"] = "invoice-1" }, cancellationToken: budget.Token);
    if (refund.IsError != true || !refund.Content.OfType<TextContentBlock>().Any(block => block.Text.Contains("Approval required", StringComparison.Ordinal)))
        throw new InvalidOperationException("Refund was not blocked.");
    Console.WriteLine("invoice.refund  -> REQUIRE_APPROVAL | blocked; approval workflow unavailable");
    return 0;
}
catch (Exception exception)
{
    // Do not print exception messages, arguments or tool responses.
    Console.Error.WriteLine($"Demo failed ({exception.GetType().Name}).");
    return 1;
}
