using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NixManager;

/// <summary>
/// Runic's log on standard error, beside the manager's own messages, where the journal keeps the tray's: a command or
/// setter that threw, and errors in the dashboard's page (event 1150 <c>PageError</c>, which the page reports with
/// <c>installErrorReporting</c>). Standard output stays the command line's and serve-only mode's
/// (<c>RUNIC_APPLICATION_URL=</c>).
/// </summary>
/// <remarks>Without a registered logger factory, Runic writes its log to <see cref="System.Diagnostics.Trace"/>, which nothing reads.</remarks>
public static class Logging
{
    /// <summary>The least severe entry written.</summary>
    public const LogLevel MinimumLevel = LogLevel.Warning;

    /// <summary>Registers the logger factory that Runic logs through, writing to <paramref name="writer"/> or standard error.</summary>
    public static IServiceCollection AddNixManagerLogging(this IServiceCollection services, TextWriter? writer = null) =>
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(MinimumLevel);
            logging.AddProvider(new TextLoggerProvider(writer));
        });
}

/// <summary>Writes each entry as one line, <c>Level Category[EventId]: message</c>, followed by its exception.</summary>
internal sealed class TextLoggerProvider(TextWriter? writer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TextLogger(categoryName, writer ?? Console.Error);

    public void Dispose() { }

    private sealed class TextLogger(string category, TextWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{logLevel} {category}[{eventId.Id}]: {formatter(state, exception)}";
            // One write per entry, so entries of different threads do not interleave.
            writer.WriteLine(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }
    }
}
