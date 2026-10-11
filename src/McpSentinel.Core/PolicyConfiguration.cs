namespace McpSentinel.Core;

/// <summary>An immutable, validated snapshot of policy rules.</summary>
public sealed class PolicyConfiguration
{
    public PolicyAction DefaultAction => PolicyAction.Deny;
    public IReadOnlyList<PolicyRule> Rules { get; }

    public PolicyConfiguration(IEnumerable<PolicyRule> rules, PolicyAction defaultAction = PolicyAction.Deny)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (defaultAction != PolicyAction.Deny)
            throw new ArgumentException("The default action must be Deny.", nameof(defaultAction));

        var snapshot = rules.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in snapshot)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.ToolName))
                throw new ArgumentException("Every rule must have a nonblank tool name.", nameof(rules));
            if (!Enum.IsDefined(rule.Action))
                throw new ArgumentException("Unsupported policy action.", nameof(rules));
            if (!names.Add(rule.ToolName))
                throw new ArgumentException($"Duplicate rule for '{rule.ToolName}'.", nameof(rules));
        }
        Rules = Array.AsReadOnly(snapshot);
    }
}
