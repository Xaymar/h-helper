using System.Diagnostics;

namespace HHelper.App;

/// <summary>Autostart through a logon scheduled task with highest privileges (no service, no UAC prompt at logon).</summary>
internal static class Autostart
{
    private const string TaskName = "H-Helper";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    public static void Set(bool enabled)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");
        var code = enabled
            ? Run("/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/TR", $"\"{exe}\"")
            : Run("/Delete", "/F", "/TN", TaskName);
        if (code != 0) throw new InvalidOperationException($"schtasks exited with code {code}.");
    }

    private static int Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
