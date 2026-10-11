using System.Text.Json;

namespace McpSentinel.Core;

/// <summary>Parses strict policy JSON. Invalid input throws; no fallback policy is returned.</summary>
public static class PolicyConfigurationJson
{
    public static PolicyConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = ReadObject(document.RootElement, "defaultAction", "rules");
        var defaultAction = ReadAction(root["defaultAction"]);
        if (root["rules"].ValueKind != JsonValueKind.Array)
            throw new JsonException("Rules must be an array.");

        var rules = new List<PolicyRule>();
        foreach (var element in root["rules"].EnumerateArray())
        {
            var rule = ReadObject(element, "toolName", "action");
            if (rule["toolName"].ValueKind != JsonValueKind.String)
                throw new JsonException("Tool name must be a string.");
            rules.Add(new PolicyRule(rule["toolName"].GetString()!, ReadAction(rule["action"])));
        }

        try
        {
            return new PolicyConfiguration(rules, defaultAction);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("Invalid policy configuration.", exception);
        }
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a policy object.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !properties.TryAdd(property.Name, property.Value))
                throw new JsonException($"Unknown or duplicate field '{property.Name}'.");
        }
        if (properties.Count != fields.Length)
            throw new JsonException("Missing required policy fields.");
        return properties;
    }

    private static PolicyAction ReadAction(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new JsonException("Action must be a supported string.");
        return element.GetString() switch
        {
            "Allow" => PolicyAction.Allow,
            "Deny" => PolicyAction.Deny,
            "RequireApproval" => PolicyAction.RequireApproval,
            _ => throw new JsonException("Unsupported policy action.")
        };
    }
}
