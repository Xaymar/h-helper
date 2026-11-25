using System.Text.Json;
using HHelper.Core.Broker;
using HHelper.Core.Settings;

namespace HHelper.App;

/// <summary>
/// Read-only report: identity, diagnostics, a few telemetry samples, raw battery values and the
/// fan tables. Only allowlisted instances are read, through the same broker as the app, and
/// nothing is written (crash-recovery restores are the one exception, as on a normal start).
/// </summary>
internal static class Report
{
    public static int Write(string path)
    {
        using var host = new HardwareHost(new SettingsStore(SettingsStore.DefaultPath));
        host.Start(startPolling: false);

        var events = new List<MsiEvent>();
        host.EventLogged += e => { lock (events) events.Add(e); };

        var samples = new List<Telemetry>();
        object? battery = null;
        Dictionary<string, FanCurve>? fans = null;
        Diagnostics? diagnostics = null;
        string? error = null;

        if (host.Broker is { } broker)
        {
            try
            {
                diagnostics = broker.GetDiagnostics();
                for (var i = 0; i < 5; i++)
                {
                    samples.Add(broker.GetTelemetry());
                    Thread.Sleep(2000);
                }
                var (main, second) = broker.GetBatteryExtras();
                battery = new { main, second };
                fans = broker.Fans.ToDictionary(f => f, broker.GetFanCurve);
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        }

        var report = new
        {
            tool = "H-Helper",
            version = typeof(Report).Assembly.GetName().Version?.ToString(),
            takenAt = DateTimeOffset.Now,
            status = host.Status,
            identity = host.Identity,
            diagnostics,
            telemetry = samples,
            battery,
            fans,
            events,
            error,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return host.Broker is null || error is not null ? 1 : 0;
    }
}
