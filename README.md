# H-Helper

An open-source control tool for MSI laptops, in the spirit of G-Helper for ASUS: one small tray
app instead of MSI Center. Windows only. GPL-3.0-or-later.

> **Status: v0.1, read-only first.** H-Helper v1 talks to the laptop only through the firmware's
> own WMI data blocks. There is no kernel driver and no raw embedded-controller (EC) port access.
> The only supported model so far is the **MSI Alpha 15 B5EEK (board MS-158L)**.

## What v1 does

| Feature | State |
| --- | --- |
| CPU temperature, GPU/system temperature | Read |
| CPU and GPU fan speed (raw fan-table value) and tachometer (raw) | Read; no RPM conversion until it is confirmed |
| AC adapter flag (cross-check against Windows) | Read |
| Hotkey and state-change events (Fn keys, brightness, mode changes) | Logged; related values re-read |
| EC firmware string, mode selectors, flag bytes, raw battery values | Diagnostics tab |
| Graphics mode (hybrid / disabled, from the AMD setup variable) | Read-only line in diagnostics |
| Custom fan curves | Experimental, behind a switch, **disabled by the MS-158L profile** until the fan table is confirmed on hardware |
| Report file (`HHelper.exe --report [file.json]`) | Read-only dump for bug reports and new models |
| Autostart | Logon scheduled task, elevated |

Not in v1: Cooler Boost, performance modes, battery charge limit, GPU/MUX switching. On MS-158L
these are not exposed through WMI and would need raw EC access, which brings back the security
problem H-Helper exists to remove. See `docs/specs/ms-158l-stage-b.md` section 4.

## Requirements

- Windows 10 or 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Administrator rights: the MSI WMI blocks refuse normal users
- MSI Center does **not** need to be installed. H-Helper addresses the firmware's WMI blocks by
  GUID, so it does not depend on the class descriptions MSI Center installs.

## Safety model

All hardware access goes through one component, `MsiBroker` (`src/HHelper.Core/Broker`). The UI
can only make typed requests (telemetry, diagnostics, battery extras, fan curve read/write,
restore). There is no "read or write any instance" call.

- **Allowlist, not enumeration.** Each model profile lists exactly which (block, instance) pairs
  may be read. Anything else is refused. Blocks are never enumerated.
- **Hard rules in code**, independent of profiles (`SafetyRules.cs`): instances whose read has side
  effects are refused even if a profile lists them, and writes are limited to the fan-table
  instances of two blocks. A profile that breaks a rule fails to load.
- **Model gate.** Nothing is read unless the SMBIOS board ID matches a profile.
- **Writes** need all of: the profile enables them, the user typed this laptop's board ID into
  Settings › Experimental controls, the value passes validation against the factory table. The
  factory table is saved to `%ProgramData%\H-Helper\snapshots` before the first write, every
  write is read back, and any mismatch restores the factory table. On exit (and on the next start
  after a crash) the factory table is restored unless "keep curve" is on.

## Adding a laptop

Every model difference lives in a JSON profile under `profiles/`, never in code. A profile names
the boards it matches, the WMI blocks by GUID, the allowlist, event codes and fan-table layout.
Each profile must be backed by a written spec in `docs/specs/` that says which instances are safe.
Run `HHelper.exe --report` on an unsupported laptop: it reads nothing hardware-specific there, but
the identity it records is the starting point.

## Building

```
dotnet build HHelper.sln -c Release
dotnet test tests/HHelper.Core.Tests
dotnet publish src/HHelper.App -c Release -p:PublishSingleFile=true -o publish
```

The core library and its tests also build and run on Linux; the app builds there with
`EnableWindowsTargeting` but only runs on Windows.

## Layout

```
profiles/             model profiles (JSON, embedded into the core library)
docs/specs/           functional specs the profiles and code are written from
src/HHelper.Core/     broker, safety rules, profiles, Win32 WMI transport (no UI)
src/HHelper.App/      WinForms tray app, layout built in code
tests/                unit tests against a fake transport
```

## How the hardware knowledge is obtained

Interfaces without public documentation are handled clean-room: one person observes the hardware,
a second writes a functional spec from those notes (no code, no vendor text), and a third writes
the code from the spec alone. Only the spec enters this repository. H-Helper contains no code or
text from MSI software.
