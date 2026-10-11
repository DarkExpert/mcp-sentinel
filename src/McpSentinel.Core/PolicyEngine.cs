using System.Collections.Frozen;

namespace McpSentinel.Core;

/// <summary>Evaluates exact tool names against an immutable policy snapshot.</summary>
public sealed class PolicyEngine
{
    private readonly FrozenDictionary<string, PolicyRule> rules;

    public PolicyEngine(PolicyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        rules = configuration.Rules.ToFrozenDictionary(rule => rule.ToolName, StringComparer.Ordinal);
    }

    public PolicyEvaluationResult Evaluate(string? toolName)
    {
        if (!string.IsNullOrWhiteSpace(toolName) && rules.TryGetValue(toolName, out var rule))
            return new(toolName, rule.Action, rule);

        return new(toolName, PolicyAction.Deny, null);
    }
}
