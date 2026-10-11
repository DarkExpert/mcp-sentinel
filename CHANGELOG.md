# Changelog

## Unreleased — v0.1.0 preparation

### Added

- .NET 10 solution using the official MCP SDK 2.2.0.
- Immutable, concurrent-safe policy engine with strict JSON validation and Deny defaults.
- Streamable HTTP Gateway enforcing Allow, Deny and RequireApproval before upstream dispatch.
- In-memory three-tool MCP DemoServer and SDK console demo client.
- Mixed-protocol result adaptation, bounded operations and continuation integration tests.
- Sanitized structured policy/terminal audit events with per-invocation correlation IDs.
- PowerShell public demo runner with readiness signals, counter verification and owned-process cleanup.
- Ubuntu GitHub Actions Release build, tests and demo workflow.
- Architecture, local demo instructions and release notes draft.

### Security boundaries

- No authentication, approval workflow, database, durable audit storage or production deployment support.
- Trusted local upstream only; direct upstream access bypasses Gateway policy.
- SDK continuation may repeat upstream invocations. No exactly-once execution claim.

No release or tag has been created by this development story.
