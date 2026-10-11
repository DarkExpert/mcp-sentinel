# MCP Sentinel

MCP Sentinel is an open-source security and policy gateway for AI agents communicating through the Model Context Protocol (MCP). The solution targets .NET 10; Gateway and DemoServer reference the official `ModelContextProtocol.AspNetCore` 2.2.0 package.

MS-002 implements policy evaluation in `McpSentinel.Core`: immutable rules, exact ordinal case-sensitive tool-name matching, strict JSON configuration validation, and concurrent evaluation. MS-003 adds a real Streamable HTTP MCP Gateway that discovers a trusted local upstream server's tools and enforces policy before forwarding tool calls. This is a development demonstration, not a production-ready security boundary.

The policy model fails closed: the default must be `Deny`, and unknown, null, empty or whitespace names always yield `Deny`. Invalid configuration throws instead of installing a fallback policy. `RequireApproval` is a distinct decision and never permission to execute.

The demo policy in `policies/demo-policy.json` allows `customer.read`, denies `customer.delete`, and requires approval for `invoice.refund`. All other names are denied. JSON requires `defaultAction` and `rules`; each rule requires `toolName` and `action`. Action strings are exactly `Allow`, `Deny` or `RequireApproval`. Duplicate rules/properties, unknown fields, missing fields and invalid values are rejected. An empty rules array is valid and denies every tool.

To load and evaluate the demo policy from the repository root:

```csharp
using McpSentinel.Core;

var json = File.ReadAllText("policies/demo-policy.json");
var engine = new PolicyEngine(PolicyConfigurationJson.Parse(json));
var decision = engine.Evaluate("invoice.refund"); // RequireApproval
```

The caller owns file access and must stop initialization if reading or parsing fails. Core only parses supplied JSON and evaluates policies; the Gateway supplies file loading and enforcement.

## Run locally

From the repository root, start the DemoServer first:

```shell
dotnet run --project samples/McpSentinel.DemoServer --launch-profile http
```

In a second terminal, start the Gateway:

```shell
dotnet run --project src/McpSentinel.Gateway --launch-profile http
```

| Component | Local endpoint |
|---|---|
| Gateway (connect MCP clients here) | `http://localhost:5094/mcp` |
| DemoServer (trusted upstream) | `http://localhost:5119/mcp` |

Both servers bind to localhost by default. Configure a Streamable HTTP connection in your MCP client using the Gateway URL; no authentication headers are required. With the official C# SDK 2.2.0, the client flow is:

```csharp
using ModelContextProtocol.Client;

await using var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("http://localhost:5094/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp
});
await using var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
var result = await client.CallToolAsync("customer.read",
    new Dictionary<string, object?> { ["customerId"] = "customer-1" });
```

| Tool | Arguments | Gateway behavior |
|---|---|---|
| `customer.read` | `customerId: "customer-1"` | Allow: forwards and returns mock customer data |
| `customer.delete` | `customerId: "customer-1"` | Deny: error result; upstream does not execute |
| `invoice.refund` | `invoiceId: "invoice-1"` | RequireApproval: error result; upstream does not execute |
| Any unknown or differently cased name | — | Denied |

Discovery includes all three upstream tools and their metadata. Discovery does not grant permission to execute. The DemoServer uses only in-memory mock data and per-host thread-safe invocation counters.

`Gateway:UpstreamUrl` is provided **only in appsettings.Development.json** for the local demo. Outside Development, it must be explicitly supplied, for example via `Gateway__UpstreamUrl`. It must be an absolute loopback HTTP(S) URL without credentials, query or fragment. Redirects and system proxies are disabled for the upstream connection. `Gateway__PolicyPath` optionally selects a policy file; the default is the demo policy copied beside the Gateway output under `policies/`. Relative override paths resolve against the process working directory.

The Gateway reads and validates policy and connects/discovers upstream tools before opening its listener. Missing/unreadable/invalid policy, missing/invalid endpoint, and upstream initialization or discovery failures abort startup. There is no fallback policy. The 10-second upstream connection/discovery startup budget **begins after policy loading and validation**; policy loading receives caller cancellation but has no separate timeout. Forwarded upstream operations have a 10-second budget, passed to the SDK with caller cancellation.

## Protocol compatibility and continuation

For this demo, downstream MCP revisions `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25` and `2026-07-28` are supported by SDK negotiation and tested. The upstream client prefers `2026-07-28` and may negotiate an older SDK-supported revision. The Gateway creates a fresh result preserving content, error status and metadata. It copies the content collection and clones structured content/metadata, without modifying the upstream result. It leaves the completed result discriminator unset for the downstream SDK to stamp according to that request's revision: `resultType: "complete"` for `2026-07-28`, absent for older revisions. Structured content is retained from `2025-06-18` onward; for the two older revisions its JSON is appended as text content instead. Unsupported result discriminators fail as errors instead of being reinterpreted as successful results.

There are **no application-level tool retries or exactly-once execution guarantees**. SDK 2.2.0's `CallToolAsync` automatically continues an upstream request on an `input_required` result carrying `requestState`. No public SDK option safely disables this behavior; pinning an older protocol is not a reliable rejection mechanism. This MVP retains SDK behavior rather than adding custom protocol interception.

Policy is evaluated once per downstream handler invocation. An allowed call can produce multiple upstream invocations inside that SDK call, carrying the same name and arguments plus continuation state. Tests observe two invocations for one continuation and 11 total invocations before the SDK's ten-retry limit yields an error. A malformed continuation without state/input fails without redispatch. Deny, RequireApproval and unknown tools never enter the upstream SDK call and cannot start a continuation. The operation budget covers all its continuation rounds. No sampling, elicitation or roots handlers are registered upstream; continuations requiring those inputs fail rather than invoking downstream client workflows.

The trusted upstream is responsible for continuation semantics and avoiding repeated side effects. A tool that commits an effect and then requests another invocation may repeat effects. An error after continuation exhaustion does not prove that no effect occurred. The shipped DemoServer does not request continuations.

## Security limitations

There is no authentication, user authorization, approval workflow, durable audit log, rate limiting or policy reload. `RequireApproval` always blocks. Local processes can connect directly to the DemoServer and bypass the Gateway; configure clients to use the Gateway. Loopback binding and restricted Host names are development defaults, not access control. Do not expose either server on a public interface or treat this demo as production-ready.

Only tool discovery and tool calls are proxied. Resources, prompts, sampling, elicitation, progress notifications and task workflows are not relayed. Tool metadata is trusted local upstream data and is snapshotted at startup; restart after tool or policy changes. Compatibility tests cover the demo schemas and controlled text/structured results; arbitrary newer schemas or content types may need additional version adaptation. Tool-name policy does not validate tool arguments or sanitize upstream results. SDK diagnostics are disabled to avoid logging full arguments/results; credentials are neither accepted in the upstream URL nor forwarded from downstream clients. Timeouts/cancellation cannot undo a side effect that an allowed upstream tool has already committed.

With the .NET 10 SDK installed, run from the repository root:

```shell
dotnet restore
dotnet build
dotnet test
```

Tests include Core policy cases and isolated integration tests with actual SDK clients and Kestrel hosts on dynamic loopback ports. No external services or arbitrary startup sleeps are required.

See [architecture](docs/architecture.md) for the Gateway design and current limitations.
