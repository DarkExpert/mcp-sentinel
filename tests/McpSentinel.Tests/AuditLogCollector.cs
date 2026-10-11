using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace McpSentinel.Tests;

internal sealed class AuditLogCollector : ILoggerProvider
{
    private readonly TaskCompletionSource<AuditEntry> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<AuditEntry> Completion => completion.Task;
    public ConcurrentQueue<AuditEntry> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CollectorLogger(this, categoryName);
    public void Dispose() { }

    private sealed class CollectorLogger(AuditLogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (category != "McpSentinel.Gateway.SecurityAudit") return;
            var entry = new AuditEntry(eventId.Id,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value),
                formatter(state, exception), exception);
            owner.Entries.Enqueue(entry);
            if (eventId.Id == 1001) owner.completion.TrySetResult(entry);
        }
    }
}

internal sealed record AuditEntry(int EventId, Dictionary<string, object?> Fields, string Message, Exception? Exception);
