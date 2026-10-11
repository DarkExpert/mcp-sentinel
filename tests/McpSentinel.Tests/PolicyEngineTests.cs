using System.Text.Json;
using McpSentinel.Core;

namespace McpSentinel.Tests;

public sealed class PolicyEngineTests
{
    private static PolicyEngine CreateEngine() => new(new PolicyConfiguration([
        new("customer.read", PolicyAction.Allow),
        new("customer.delete", PolicyAction.Deny),
        new("invoice.refund", PolicyAction.RequireApproval)
    ]));

    [Theory]
    [InlineData("customer.read", PolicyAction.Allow)]
    [InlineData("customer.delete", PolicyAction.Deny)]
    [InlineData("invoice.refund", PolicyAction.RequireApproval)]
    public void EvaluatesExplicitRules(string name, PolicyAction action)
    {
        var result = CreateEngine().Evaluate(name);
        Assert.Equal(action, result.Action);
        Assert.Equal(name, result.ToolName);
        Assert.Equal(new PolicyRule(name, action), result.MatchedRule);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("Customer.read")]
    [InlineData("CUSTOMER.READ")]
    [InlineData(" customer.read")]
    [InlineData("customer.read ")]
    public void DeniesUnmatchedOrBlankNames(string? name)
    {
        var result = CreateEngine().Evaluate(name);
        Assert.Equal(PolicyAction.Deny, result.Action);
        Assert.Null(result.MatchedRule);
    }

    [Fact]
    public void RejectsDuplicateRules() => Assert.Throws<ArgumentException>(() =>
        new PolicyConfiguration([new("tool", PolicyAction.Allow), new("tool", PolicyAction.Deny)]));

    [Fact]
    public void AllowsDistinctCaseSensitiveRules()
    {
        var engine = new PolicyEngine(new PolicyConfiguration([
            new("tool", PolicyAction.Allow), new("Tool", PolicyAction.RequireApproval)]));
        Assert.Equal(PolicyAction.Allow, engine.Evaluate("tool").Action);
        Assert.Equal(PolicyAction.RequireApproval, engine.Evaluate("Tool").Action);
    }

    [Theory]
    [InlineData(PolicyAction.Allow)]
    [InlineData(PolicyAction.RequireApproval)]
    [InlineData((PolicyAction)99)]
    public void RejectsNonDenyDefaults(PolicyAction action) => Assert.Throws<ArgumentException>(() =>
        new PolicyConfiguration([], action));

    [Fact]
    public void RejectsUndefinedAction() => Assert.Throws<ArgumentException>(() =>
        new PolicyConfiguration([new("tool", (PolicyAction)99)]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsInvalidRuleNames(string? name) => Assert.Throws<ArgumentException>(() =>
        new PolicyConfiguration([new(name!, PolicyAction.Allow)]));

    [Fact]
    public void RejectsNullConfigurationAndRules()
    {
        Assert.Throws<ArgumentNullException>(() => new PolicyEngine(null!));
        Assert.Throws<ArgumentNullException>(() => new PolicyConfiguration(null!));
        Assert.Throws<ArgumentException>(() => new PolicyConfiguration([null!]));
        Assert.Throws<ArgumentNullException>(() => PolicyConfigurationJson.Parse(null!));
    }

    [Fact]
    public void ConfigurationSnapshotsSourceCollection()
    {
        var source = new List<PolicyRule> { new("tool", PolicyAction.Allow) };
        var configuration = new PolicyConfiguration(source);
        source[0] = new("tool", PolicyAction.Deny);
        source.Clear();
        Assert.Throws<NotSupportedException>(() => ((IList<PolicyRule>)configuration.Rules).Clear());
        Assert.Equal(PolicyAction.Allow, new PolicyEngine(configuration).Evaluate("tool").Action);
        Assert.Equal(PolicyAction.Deny, configuration.DefaultAction);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"Deny\"}")]
    [InlineData("{\"defaultAction\":\"Allow\",\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"RequireApproval\",\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"deny\",\"rules\":[]}")]
    [InlineData("{\"defaultAction\":1,\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":null}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"tool\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"action\":\"Allow\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":null,\"action\":\"Allow\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\" \",\"action\":\"Allow\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"tool\",\"action\":\"Execute\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"tool\",\"action\":0}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"tool\",\"action\":\"Allow\"},{\"toolName\":\"tool\",\"action\":\"Deny\"}]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"defaultAction\":\"Deny\",\"rules\":[]}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[],\"extra\":true}")]
    [InlineData("{\"defaultAction\":\"Deny\",\"rules\":[{\"toolName\":\"tool\",\"action\":\"Allow\",\"action\":\"Deny\"}]}")]
    public void RejectsInvalidJsonConfiguration(string json) =>
        Assert.ThrowsAny<JsonException>(() => PolicyConfigurationJson.Parse(json));

    [Fact]
    public void LoadsDemoPolicyFromJson()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "policies", "demo-policy.json"));
        var configuration = PolicyConfigurationJson.Parse(json);
        var engine = new PolicyEngine(configuration);
        Assert.Equal(3, configuration.Rules.Count);
        Assert.Equal(PolicyAction.Allow, engine.Evaluate("customer.read").Action);
        Assert.Equal(PolicyAction.Deny, engine.Evaluate("customer.delete").Action);
        Assert.Equal(PolicyAction.RequireApproval, engine.Evaluate("invoice.refund").Action);
        Assert.Equal(PolicyAction.Deny, engine.Evaluate("unknown").Action);
    }

    [Fact]
    public void EmptyPolicyDeniesEverything()
    {
        var engine = new PolicyEngine(PolicyConfigurationJson.Parse("{\"defaultAction\":\"Deny\",\"rules\":[]}"));
        Assert.Equal(PolicyAction.Deny, engine.Evaluate("tool").Action);
    }

    [Fact]
    public async Task ConcurrentEvaluationsRemainConsistent()
    {
        var engine = CreateEngine();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                Assert.Equal(PolicyAction.Allow, engine.Evaluate("customer.read").Action);
                Assert.Equal(PolicyAction.Deny, engine.Evaluate("customer.delete").Action);
                Assert.Equal(PolicyAction.RequireApproval, engine.Evaluate("invoice.refund").Action);
                Assert.Equal(PolicyAction.Deny, engine.Evaluate("unknown").Action);
                Assert.Equal(PolicyAction.Deny, engine.Evaluate(null).Action);
            }
        })));
    }
}
