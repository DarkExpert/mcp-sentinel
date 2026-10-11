# Gateway architecture

MCP Sentinel sits between an MCP client and one trusted local upstream MCP server. The Gateway receives SDK-decoded tool requests, evaluates the exact requested name through Core, and forwards only explicit Allow decisions. Results return through the Gateway with upstream error flags intact.

```text
AI agent / MCP client
        |
        v
McpSentinel.Gateway -- policy evaluation --> McpSentinel.Core
        |
        | Forward only an explicit Allow for a discovered tool
        v
Upstream MCP server (DemoServer for local examples)
```

## MS-002 scope

MS-002 implements policy evaluation only, not enforcement. MS-003 supplies enforcement in the Gateway host; Core remains unchanged and independent of protocol and infrastructure.

Core depends only on standard .NET libraries. It has no ASP.NET Core, MCP SDK, file-system access, database or authentication dependency. `PolicyConfigurationJson.Parse` accepts JSON supplied by a caller. The host owns reading `policies/demo-policy.json` and handling file/parse errors; invalid configuration must prevent startup or policy activation, never trigger a permissive fallback.

`PolicyAction` defines `Allow`, `Deny` and `RequireApproval`. Immutable `PolicyRule` and `PolicyEvaluationResult` records describe rules and decisions. `PolicyConfiguration` validates and snapshots its rules into a read-only collection. `PolicyEngine` builds a frozen dictionary with `StringComparer.Ordinal`, enabling concurrent reads without shared mutable evaluation state.

Tool names are matched exactly without case folding or trimming. Unknown, null, empty and whitespace names always return `Deny`. The only supported default is `Deny`; explicit non-deny defaults are rejected. Duplicate tool names are rejected using the same ordinal comparison used during evaluation. Case-distinct names are distinct rules. JSON uses case-sensitive fields and action strings and rejects missing/unknown fields, duplicate properties, unsupported actions and malformed input. An empty rules array is a valid deny-all policy.

An evaluation result includes the input name, decision and matched rule (null when no rule matched). A matched deny rule and a default denial can therefore be distinguished. `RequireApproval` carries no execution authorization, and no approval workflow exists. There is no configuration reload mechanism: a host would need to validate a fresh configuration and replace the entire engine snapshot.

## MS-003 implementation

Both hosts use `AddMcpServer`, `WithHttpTransport` with stateless sessions, and `MapMcp("/mcp")`. The SDK owns protocol parsing, initialization/discovery handshakes, capability negotiation and serialization. There is no hand-written JSON-RPC or substitute REST interface. The Gateway supports the tools capability only; it does not claim to relay all MCP operations.

Before constructing a listening host, `GatewayHost.BuildAsync` validates a loopback upstream URL, reads and parses policy, creates an SDK `McpClient` over `HttpClientTransport` explicitly in Streamable HTTP mode, and uses `ListToolsAsync` to discover all pages. Failure at any step throws and prevents the listener from starting. Invalid or duplicate upstream tool names are rejected. Discovery is a startup snapshot, not a dynamic catalog.

`WithListToolsHandler` returns the discovered protocol tool metadata, preserving names, descriptions and schemas. `WithCallToolHandler` is the only Gateway tool execution path; no local tools or assembly-wide discovery are registered. It evaluates every invocation, including calls made without prior listing. Only `PolicyAction.Allow` proceeds, and the name must also appear in the validated upstream catalog. Deny and RequireApproval return `CallToolResult` with `IsError = true` without contacting upstream.

Allowed requests forward their name and arguments through the typed `CallToolAsync(CallToolRequestParams, CancellationToken)` overload. Request metadata, task options and progress tokens are not forwarded. Protocol/transport exceptions propagate through the SDK's error path. No tool call is retried by application code, but SDK continuation can reissue an allowed request; exactly-once execution is not guaranteed.

The 10-second connection/discovery startup budget begins after policy loading and validation. Policy loading receives caller cancellation but has no separate timeout. Each forwarded operation, including all SDK continuation rounds, has a 10-second budget linked to caller cancellation. The upstream HTTP client also has a 10-second timeout, no automatic redirects and no system proxy. The host owns async disposal of the upstream client and transport. SDK diagnostic logging is disabled because it can include arguments or results; the application does not log credentials or full arguments.

### Result adaptation

The upstream SDK client defaults to `2026-07-28`, while downstream requests negotiate independently. Returning its result object directly leaked the newer `resultType` field into older downstream responses. `AdaptResult` creates a new `CallToolResult`, preserves `Content`/`IsError`, clones `StructuredContent` and `_meta`, and leaves `ResultType` null. The installed SDK 2.2.0 contract defines absent/null and `complete` as equivalent completed results; its `SetWithAlternateHandler` stamps `complete` only for `2026-07-28+` downstream requests. This is SDK-directed wire shaping, not global deletion of protocol fields. Any remaining non-complete discriminator is rejected as an error.

Downstream revisions `2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25` and `2026-07-28` are tested for success and tool-error wire responses with an upstream negotiating `2026-07-28`. The two pre-June-2025 revisions lack `structuredContent`, so its JSON data is appended to compatible text content. June 2025 onward preserves the structured field. Tests observe actual HTTP JSON/SSE response bodies through a capture-only HTTP handler; SDK clients still generate requests and interpret responses. General translation of arbitrary version-specific schemas/content types remains outside the tested demo scope.

### SDK continuation boundary

SDK 2.2.0's `McpClientImpl.SendRequestAsync` recognizes `input_required` results before `CallToolAsync` returns. A state-only continuation reissues the original tool call with `requestState` and the same tool name/arguments. There is no public continuation-disable option in `McpClientOptions`; its handlers configure input resolution, not state-only retry suppression. Older protocol pinning merely warns for unexpected continuations and does not disable this loop. No unsupported SDK workaround or custom protocol interceptor is installed.

The Gateway evaluates policy once per downstream handler entry, before that SDK operation. SDK continuation rounds do not re-enter Gateway policy evaluation. With an immutable policy and unchanged tool name, this does not grant a denied tool execution. It can cause repeated upstream handler invocations and requires trusted upstream continuation semantics to avoid repeated side effects. The shipped DemoServer has no continuation behavior.

Controlled upstream tests observe two invocations for one state continuation and 11 invocations for exhaustion of the SDK's ten-retry limit; exhaustion returns an error. Missing state/input fails after one invocation. Deny, RequireApproval and unknown tools yield zero upstream invocations even against a continuation-capable upstream. Tests count downstream handler entries separately without replacing the enforcement handler. No upstream input-resolution handlers are registered, so sampling/elicitation/roots continuations fail rather than reaching downstream workflows. All rounds share the operation cancellation token and budget; cancellation is not transaction rollback. An eventual error cannot establish that preceding invocations had no side effects.

DemoServer explicitly registers one `DemoTools` instance with `[McpServerTool(Name = ...)]` names matching policy entries. It maintains mock customers/invoices and thread-safe counters per host. Counters are accessible to in-process tests, not exposed through MCP or an HTTP management endpoint. A missing customer produces a genuine upstream tool error for propagation tests.

Integration tests run actual SDK clients through Gateway and DemoServer over Kestrel on ephemeral loopback ports. Enforcement tests use the standard `2025-06-18` handshake; protocol tests also cover the other four SDK-supported revisions. Assertions verify exact names/schema metadata, one mock read execution, zero blocked executions, configuration failures, startup/runtime upstream failures and upstream error preservation. Controlled upstream handlers provide deterministic signals and invocation counts for cancellation/timeout tests, plus counters for continuation behavior.

## Local configuration and limitations

The DemoServer defaults to `http://localhost:5119/mcp`; the Gateway defaults to `http://localhost:5094/mcp`. The upstream URL exists only in Development configuration and must otherwise be supplied explicitly. Policy defaults to the copied output `policies/demo-policy.json`, with an optional `Gateway:PolicyPath` override. See README for startup commands and client examples.

This is a local development gateway without authentication or an approval workflow. Anyone able to reach the endpoints can call them, and direct access to DemoServer bypasses Gateway enforcement. Do not expose the demo publicly. Loopback binding and restricted Host names reduce accidental exposure but do not establish caller identity. Tool metadata and outputs are trusted upstream content; no argument-based policy or response filtering exists. Cancellation is cooperative and cannot roll back already performed effects.

Authentication, durable audit records, approval workflows, policy reloads and other MCP operations remain future work. Supporting multiple upstream servers would require explicit namespacing and identity mapping; this iteration supports exactly one trusted local upstream. No production-readiness claim is made.
