# MCP Sentinel v0.1.0 — release notes draft

**Draft; not published.**

MCP Sentinel demonstrates policy enforcement between an MCP client and a trusted local MCP server. Agents can discover tools while the Gateway permits only explicitly allowed tool calls.

## Included

- .NET 10 and official MCP SDK 2.2.0.
- Strict, immutable policy configuration and default-deny tool matching.
- Real Streamable HTTP tool discovery/forwarding and distinct approval-required blocking.
- Protocol-aware success/error results and tested SDK continuation behavior.
- Structured security audit events excluding arguments, response content and exception messages.
- Console demo and PowerShell runner checking actual mock upstream execution counters.
- Automated policy/integration/audit tests and Ubuntu GitHub Actions workflow.

## Try it

```powershell
pwsh -File ./scripts/run-demo.ps1
```

See [local instructions](demo.md). The scenario reads a mock customer, blocks deletion and blocks a refund pending an unavailable approval workflow.

## Limitations

This is a development MVP, not production-ready. It has no authentication, user authorization, approval workflow, durable audit storage, rate limiting or reload. Direct DemoServer access bypasses policy. SDK continuation can repeat allowed invocations; cancellation cannot undo effects. Only tool discovery/calls are proxied, and upstream metadata/results are trusted.

## Before publication

- [ ] Review the security boundary and audit redaction semantics.
- [x] Confirm Release restore/build/tests and the complete demo in an approved execution environment (Ubuntu WSL, .NET 10.0.401; 98 tests passed).
- [ ] Confirm Ubuntu CI passes, including the demo and failure cleanup checks.
- [ ] Confirm no binaries, secrets or local absolute paths are tracked.
- [ ] Approve the maintainer contact URL placeholder.
- [ ] Finalize this draft and changelog, then separately approve tag/release creation.

MIT licensed. No tag or GitHub release is created by these notes.
