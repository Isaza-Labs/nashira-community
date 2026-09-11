using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using nashira_backend.Services.Errors;

namespace nashira_backend.Tests;

public class UnhandledExceptionHandlerTests
{
    [Fact]
    public async Task Unhandled_exceptions_are_logged_and_return_a_generic_problem()
    {
        var logger = new CapturingLogger<UnhandledExceptionHandler>();
        var handler = new UnhandledExceptionHandler(logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/ai/specs";
        context.TraceIdentifier = "trace-123";
        context.Response.Body = new MemoryStream();
        var exception = new InvalidOperationException("database blew up");

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception == exception);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("internal_server_error", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("trace-123", body.RootElement.GetProperty("trace_id").GetString());
        Assert.DoesNotContain("database blew up", body.RootElement.GetRawText());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
}
