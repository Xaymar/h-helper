using HHelper.Core.Settings;

namespace HHelper.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // "--report <file>": read-only dump for bug reports and new-model contributions.
        var reportIndex = Array.FindIndex(args, a => a.Equals("--report", StringComparison.OrdinalIgnoreCase));
        if (reportIndex >= 0)
        {
            var path = reportIndex + 1 < args.Length
                ? args[reportIndex + 1]
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "h-helper-report.json");
            return Report.Write(path);
        }

        using var mutex = new Mutex(true, @"Local\H-Helper-SingleInstance", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        using var host = new HardwareHost(new SettingsStore(SettingsStore.DefaultPath));
        host.Start();
        Application.Run(new TrayContext(host));
        return 0;
    }
}
