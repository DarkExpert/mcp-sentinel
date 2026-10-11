using System.Collections.Concurrent;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpSentinel.DemoServer;

/// <summary>Mock data and counters scoped to one server instance, not exposed as HTTP endpoints.</summary>
public sealed class DemoTools
{
    private readonly ConcurrentDictionary<string, int> invocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> customers = new(
        [new KeyValuePair<string, string>("customer-1", "Ada Example")], StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, decimal> invoices = new(
        [new KeyValuePair<string, decimal>("invoice-1", 25m)], StringComparer.Ordinal);

    public int InvocationCount(string toolName) => invocations.GetValueOrDefault(toolName);

    [McpServerTool(Name = "customer.read"), Description("Read a mock customer by ID.")]
    public CallToolResult ReadCustomer(string customerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Count("customer.read");
        return customers.TryGetValue(customerId, out var name)
            ? Result($"{customerId}: {name}") : Result("Customer not found.", true);
    }

    [McpServerTool(Name = "customer.delete"), Description("Delete a mock customer by ID.")]
    public CallToolResult DeleteCustomer(string customerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Count("customer.delete");
        return customers.TryRemove(customerId, out _) ? Result("Customer deleted.") : Result("Customer not found.", true);
    }

    [McpServerTool(Name = "invoice.refund"), Description("Refund a mock invoice by ID.")]
    public CallToolResult RefundInvoice(string invoiceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Count("invoice.refund");
        return invoices.TryRemove(invoiceId, out var amount)
            ? Result($"Mock refund: {amount}.") : Result("Invoice not found.", true);
    }

    private void Count(string name) => invocations.AddOrUpdate(name, 1, (_, count) => count + 1);

    private static CallToolResult Result(string text, bool isError = false) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }]
    };
}
