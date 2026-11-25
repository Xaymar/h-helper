namespace HHelper.Core.Broker;

/// <summary>A raw reading; null when the read failed. Units are only attached once the spec confirms them.</summary>
public sealed record Telemetry(
    DateTimeOffset At,
    int? CpuTemperatureC,
    int? GpuTemperatureC,
    int? CpuFanSpeedRaw,
    int? GpuFanSpeedRaw,
    TachReading CpuFanTach,
    TachReading GpuFanTach,
    bool? OnAc);

/// <summary>Raw tachometer value (spec 3.7). The RPM conversion is unknown (Q4); never invent one.</summary>
public sealed record TachReading(int? Raw, bool Unstable)
{
    public override string ToString() => Unstable ? "unstable" : Raw?.ToString() ?? "n/a";
}

public sealed record RawValue(string Feature, string Block, int Instance, int? Value, string? Spec, string? Note);

public sealed record Diagnostics(
    string ProfileId,
    string BoardId,
    string? EcFirmware,
    int? ModeA,
    int? ModeB,
    int? DeviceFlags,
    int? DisplayBrightness,
    int? WinFnSwapCandidate,
    string GraphicsMode,
    string? WmiInstanceBase);

/// <summary>A decoded MSI event (spec 3.8). Group is null for codes the profile does not know.</summary>
public sealed record MsiEvent(DateTimeOffset At, byte Code, byte Argument, string? Group)
{
    public override string ToString()
        => $"{At:HH:mm:ss} code 0x{Code:X2} arg 0x{Argument:X2} {(Group ?? "unknown")}";
}

public sealed class WriteRefusedException(string message) : Exception(message);

public sealed class WriteVerifyFailedException(string message) : Exception(message);
