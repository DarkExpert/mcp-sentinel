using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpSentinel.Tests;

internal sealed class FaultingAuditProvider(bool failDecision, bool failCompletion, bool failResolution = false) : ILoggerProvider
{
    public const string Secret = "LOGGER_SECRET_MUST_NOT_ESCAPE";
    private readonly bool throwDecision = failDecision;
    private readonly bool throwCompletion = failCompletion;
    private int decisionAttempts;
    private int completionAttempts;
    private int resolutionAttempts;
    private readonly TaskCompletionSource completionAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task CompletionAttempt => completionAttempt.Task;
    public int DecisionAttempts => Volatile.Read(ref decisionAttempts);
    public int CompletionAttempts => Volatile.Read(ref completionAttempts);
    public int ResolutionAttempts => Volatile.Read(ref resolutionAttempts);

    public ILogger CreateLogger(string categoryName)
    {
        if (categoryName != "McpSentinel.Gateway.SecurityAudit") return NullLogger.Instance;
        Interlocked.Increment(ref resolutionAttempts);
        if (failResolution) throw new InvalidOperationException(Secret);
        return new FaultingLogger(this);
    }

    public void Dispose() { }

    private sealed class FaultingLogger(FaultingAuditProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id == 1000)
            {
                Interlocked.Increment(ref owner.decisionAttempts);
                if (owner.throwDecision) throw new InvalidOperationException(Secret);
            }
            if (id.Id == 1001)
            {
                Interlocked.Increment(ref owner.completionAttempts);
                owner.completionAttempt.TrySetResult();
                if (owner.throwCompletion) throw new InvalidOperationException(Secret);
            }
        }
    }
}
