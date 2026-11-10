using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NixManager.Tests;

/// <summary>Runic's log, which the dashboard and the setup assistant write to standard error (<see cref="Logging"/>).</summary>
public sealed class LoggingTests
{
    [Test]
    public async Task Entries_from_warnings_up_are_written_as_one_line_each_with_their_exception()
    {
        var writer = new StringWriter();
        await using var services = new ServiceCollection().AddNixManagerLogging(writer).BuildServiceProvider();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Runic.Application.Views.Page");

        logger.LogInformation("Not written");
        logger.LogError(new EventId(1150, "PageError"), "TypeError: x is undefined");
        logger.LogWarning(new EventId(1005), new InvalidOperationException("boom"), "A callback threw");

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines[0]).IsEqualTo("Error Runic.Application.Views.Page[1150]: TypeError: x is undefined");
        await Assert.That(lines[1]).IsEqualTo("Warning Runic.Application.Views.Page[1005]: A callback threw");
        await Assert.That(lines[2]).IsEqualTo("System.InvalidOperationException: boom");
        await Assert.That(lines).Count().IsEqualTo(3);
    }
}
