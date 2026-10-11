using System.Diagnostics;
using System.Globalization;
using McpSentinel.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpSentinel.Gateway;

/// <summary>One invocation's allowlisted audit fields; never accepts arguments, results or exceptions.</summary>
internal sealed class SecurityAudit(ILogger logger, string? toolName, PolicyAction decision)
{
    private readonly string correlationId = Guid.NewGuid().ToString("N");
    private readonly long started = Stopwatch.GetTimestamp();

    public static SecurityAudit Create(IServiceProvider services, string? toolName, PolicyAction decision)
    {
        ILogger logger;
        // This guard covers only audit logger resolution, not policy or MCP execution.
        try { logger = services.GetRequiredService<ILogger<SecurityAudit>>(); }
        catch (Exception) { logger = NullLogger.Instance; }
        return new SecurityAudit(logger, toolName, decision);
    }

    public void RecordDecision()
    {
        try
        {
            logger.LogInformation(new EventId(1000, "PolicyEvaluated"),
                "PolicyEvaluated TimestampUtc={TimestampUtc} CorrelationId={CorrelationId} ToolName={ToolName} PolicyDecision={PolicyDecision}",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), correlationId, toolName, decision.ToString());
        }
        catch (Exception) { /* Audit infrastructure is best-effort; do not retry or recursively log. */ }
    }

    public void RecordCompletion(string enforcement, string upstreamStatus, string? errorCategory)
    {
        try
        {
            logger.LogInformation(
                new EventId(1001, "InvocationCompleted"),
                "InvocationCompleted TimestampUtc={TimestampUtc} CorrelationId={CorrelationId} ToolName={ToolName} PolicyDecision={PolicyDecision} EnforcementOutcome={EnforcementOutcome} UpstreamStatus={UpstreamStatus} DurationMs={DurationMs} ErrorCategory={ErrorCategory}",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), correlationId, toolName, decision.ToString(), enforcement, upstreamStatus,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, errorCategory);
        }
        catch (Exception) { /* Preserve the original execution result, exception or cancellation. */ }
    }
}
