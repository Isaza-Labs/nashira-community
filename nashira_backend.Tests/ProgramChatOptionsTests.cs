using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// The chat options as the server itself sets them up. The other tests of the idle
// limit register the options and run the start-up check themselves, so they would
// pass with either line missing from Program.cs. These run Program.cs: the server's
// own entry point, with its settings on the command line, up to the moment it has
// built its host. The host is taken from it there and the start is called off, so no
// database is asked for and nothing listens.
public class ProgramChatOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void The_server_Program_builds_reads_a_blank_idle_limit_as_off(string blank)
    {
        using var start = ProgramStart.With($"--AiChat:StreamIdleSeconds={blank}", "--AiChat:MaxIterations=7");

        Assert.True(start.Host is not null, start.WhatHappened);
        var options = start.Host!.Services.GetRequiredService<IOptions<AiChatOptions>>().Value;
        Assert.Equal(0, options.StreamIdleSeconds);
        Assert.Equal(7, options.MaxIterations);
        // Chat's own reader of the limit is built from the same options.
        using var scope = start.Host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<LlmProviderFactory>());
    }

    [Fact]
    public void The_server_Program_builds_reads_a_limit_and_the_settings_beside_it()
    {
        using var start = ProgramStart.With("--AiChat:StreamIdleSeconds=60", "--AiChat:MaxIterations=7");

        Assert.True(start.Host is not null, start.WhatHappened);
        var options = start.Host!.Services.GetRequiredService<IOptions<AiChatOptions>>().Value;
        Assert.Equal(60, options.StreamIdleSeconds);
        Assert.Equal(7, options.MaxIterations);
    }

    // With a blank limit an empty value beside it still fails, where chat first reads
    // the options, and names the setting in full.
    [Fact]
    public void The_server_Program_builds_still_refuses_an_empty_setting_beside_a_blank_limit()
    {
        using var start = ProgramStart.With("--AiChat:StreamIdleSeconds=", "--AiChat:MaxIterations=");

        Assert.True(start.Host is not null, start.WhatHappened);
        var error = Assert.Throws<InvalidOperationException>(
            () => start.Host!.Services.GetRequiredService<IOptions<AiChatOptions>>().Value);
        Assert.Contains("'AiChat:MaxIterations'", error.Message);
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("sixty")]
    [InlineData("2147483648")]
    public void Program_stops_before_building_the_server_on_a_limit_it_cannot_read(string value)
    {
        using var start = ProgramStart.With($"--AiChat:StreamIdleSeconds={value}");

        Assert.True(start.Host is null, "The server was built with AiChat:StreamIdleSeconds=" + value);
        Assert.Contains(start.Thrown, error =>
            error is InvalidOperationException && error.Message.Contains("AiChat:StreamIdleSeconds"));
    }

    // One run of the server's entry point, on a thread of its own.
    private sealed class ProgramStart : IDisposable
    {
        private readonly List<Exception> _thrown = [];
        private string? _keyRing;

        // The host Program.cs built, or null when it stopped before building one.
        public IHost? Host { get; private set; }

        // Every exception raised on the entry point's thread, caught or not. Program.cs
        // catches what stops it and logs it, so this is where a test can see it.
        public IReadOnlyList<Exception> Thrown { get { lock (_thrown) return _thrown.ToList(); } }

        public string WhatHappened => "Program.cs did not build the server. Raised on its thread: " +
            string.Join(" | ", Thrown.Select(error => $"{error.GetType().Name}: {error.Message}"));

        public static ProgramStart With(params string[] settings)
        {
            var start = new ProgramStart { _keyRing = Directory.CreateTempSubdirectory("nashira-test-keyring-").FullName };
            string[] arguments =
            [
                .. settings,
                // What Program.cs asks for before it reaches the chat options.
                "--Jwt:Key=" + Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                "--DataProtection:KeyRingPath=" + start._keyRing,
            ];
            var entryPoint = typeof(AiChatOptions).Assembly.EntryPoint
                ?? throw new InvalidOperationException("The backend assembly has no entry point.");
            // Program.cs sets the process exit code when it stops; that is this test's
            // doing, not the test run's.
            var exitCode = Environment.ExitCode;
            Exception? escaped = null;
            var thread = new Thread(() =>
            {
                var entryThread = Environment.CurrentManagedThreadId;
                void Raised(object? sender, FirstChanceExceptionEventArgs raised)
                {
                    if (Environment.CurrentManagedThreadId != entryThread) return;
                    lock (start._thrown) start._thrown.Add(raised.Exception);
                }
                // The host builder announces the host it has built. Take it, and stop
                // the entry point there, the way the framework's own test and design
                // tools stop one. A host another test builds meanwhile is on another
                // thread and is left alone.
                using var listeners = DiagnosticListener.AllListeners.Subscribe(new Each<DiagnosticListener>(listener =>
                {
                    if (listener.Name != "Microsoft.Extensions.Hosting") return;
                    listener.Subscribe(new Each<KeyValuePair<string, object?>>(announced =>
                    {
                        if (announced.Key != "HostBuilt" || announced.Value is not IHost host) return;
                        if (Environment.CurrentManagedThreadId != entryThread) return;
                        start.Host = host;
                        throw new HostAbortedException();
                    }));
                }));
                AppDomain.CurrentDomain.FirstChanceException += Raised;
                try { entryPoint.Invoke(null, [arguments]); }
                catch (TargetInvocationException error) { escaped = error.InnerException ?? error; }
                finally { AppDomain.CurrentDomain.FirstChanceException -= Raised; }
            });
            thread.Start();
            var finished = thread.Join(TimeSpan.FromSeconds(120));
            Environment.ExitCode = exitCode;
            if (!finished) throw new TimeoutException("Program.cs neither built the server nor stopped. " + start.WhatHappened);
            if (escaped is not null and not HostAbortedException)
                throw new InvalidOperationException("Program.cs let an error out. " + start.WhatHappened, escaped);
            return start;
        }

        public void Dispose()
        {
            (Host as IDisposable)?.Dispose();
            if (_keyRing is not null && Directory.Exists(_keyRing)) Directory.Delete(_keyRing, recursive: true);
        }
    }

    private sealed class Each<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
