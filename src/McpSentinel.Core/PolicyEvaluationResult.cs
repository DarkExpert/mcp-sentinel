namespace McpSentinel.Core;

/// <summary>A policy decision only; RequireApproval does not authorize execution.</summary>
public sealed record PolicyEvaluationResult(string? ToolName, PolicyAction Action, PolicyRule? MatchedRule);
