# Run the local demonstration

Prerequisites: .NET 10 SDK and PowerShell 7.2 or later on Windows, macOS or Linux. NuGet access is needed for the first restore/build; runtime requests use loopback only. No API keys, cloud services, database or pre-existing servers are required.

From the repository root:

```powershell
pwsh -File ./scripts/run-demo.ps1
```

The script builds all projects in Release and launches their DLLs directly. It chooses two available IPv4 loopback ports, waits for explicit host-started signals on stdout, runs the console client's real MCP requests against the Gateway, and verifies sanitized audit events plus DemoServer's mock invocation counters. The client never invokes DemoServer directly.

The script uses its own redirected stdin to request graceful shutdown. Its `finally` cleanup runs after success or failure, with a bounded kill fallback only for process handles it created. It never enumerates or terminates unrelated processes. Force-terminating PowerShell itself can prevent `finally` from running; close the demo normally or interrupt cooperatively. Child logs are inspected in memory, not written to log files by the script.

## Options

```powershell
# Explicit ports: fail if occupied, without stopping the occupant.
pwsh -File ./scripts/run-demo.ps1 -GatewayPort 5094 -DemoServerPort 5119

# Reuse existing Release build outputs.
pwsh -File ./scripts/run-demo.ps1 -SkipBuild

# Display sanitized terminal audit event fields.
pwsh -File ./scripts/run-demo.ps1 -SkipBuild -ShowAudit

# Failure-path check: expect a non-zero exit and owned-process cleanup.
pwsh -File ./scripts/run-demo.ps1 -SkipBuild -PolicyPath ./policies/missing-policy.json
```

Port availability probing has an unavoidable race before the child binds; a lost race causes a startup failure and cleanup, not a connection to the occupying service. Custom policies can cause the expected demo scenario to fail. Readiness has a 30-second limit, client execution 45 seconds, and graceful cleanup 10 seconds per child before the fallback. There are no arbitrary startup sleeps.

If startup fails, check SDK installation, policy readability/validity and occupied ports. Application-control software may reject locally built DLLs; resolve approval through your operating system or administrator in an approved environment. The runner does not disable security controls. A failed demonstration returns a non-zero exit code.

## Manual startup

In separate terminals:

```shell
dotnet run --project samples/McpSentinel.DemoServer --launch-profile http
dotnet run --project src/McpSentinel.Gateway --launch-profile http
dotnet run --project samples/McpSentinel.DemoClient -- http://localhost:5094/mcp
```

Manual development ports are Gateway **5094** and DemoServer **5119**, both at `/mcp`. Connect other MCP clients to the Gateway with Streamable HTTP and no authentication headers. This is a local demo only.

The upstream default `http://localhost:5119/mcp` exists only in Gateway Development settings. Outside Development supply `Gateway__UpstreamUrl` explicitly. It must be a loopback HTTP(S) URL without credentials, query or fragment; redirects and system proxies are disabled. `Gateway__PolicyPath` optionally overrides the copied output `policies/demo-policy.json`. Relative paths resolve against the process working directory.

For runner-controlled hosts, `Demo:Controlled=true` enables the private stdin shutdown channel and readiness marker. It is not an HTTP management API. The DemoServer emits counter-only diagnostic events for this mock demo, without IDs, arguments or response content. Gateway audit events are separate security events; neither provides durable evidence storage.
