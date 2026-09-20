using Serilog;
using Serilog.Core;
using Serilog.Events;
using XtreamPlayer.Infrastructure.Persistence;
using XtreamPlayer.Infrastructure.Security;

namespace XtreamPlayer.App.Logging;

/// <summary>
/// Rewrites credentials out of every rendered message before it reaches a sink.
/// </summary>
/// <remarks>
/// This is applied as a destructuring/enricher pass rather than trusting call
/// sites to redact. Xtream puts the password in every media URL and every API
/// call, so any log line that happens to include a URL leaks it — and the
/// libvlc log bridge forwards strings we did not compose at all.
/// </remarks>
public sealed class CredentialRedactingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            if (value is not ScalarValue { Value: string text })
            {
                continue;
            }

            var redacted = CredentialRedactor.Redact(text);
            if (!string.Equals(redacted, text, StringComparison.Ordinal))
            {
                logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(name, redacted));
            }
        }
    }
}

public static class SerilogSetup
{
    public static ILogger Create(bool verbose)
    {
        DatabasePaths.EnsureCreated();

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .Enrich.With<CredentialRedactingEnricher>()
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(DatabasePaths.LogDirectory, "xtream-player-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate:
                    "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        return configuration.CreateLogger();
    }
}
