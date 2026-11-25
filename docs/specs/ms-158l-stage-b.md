# H-Helper stage B functional spec: Shibela (MSI Alpha 15 B5EEK, MS-158L)

Stage: B (specification) of the clean-room process. Date: 2026-11-25. Published copy: https://claude.ai/artifact/JRhe62szFgzeWFYun1d6jv
Input: only the stage A observation notes (`stage-a-acpi-wmi-observations.md`) and the AMD_PBS_SETUP read (`pbs-setup-2026-11-25.txt`).
No firmware dumps, decompiled tables, BIOS images or MSI binaries were opened for this document, and it quotes no firmware or MSI text.
Stage C writes code from this document only. If stage C needs something this spec does not say, it asks for a new stage A observation rather than looking at the raw material.

## 0. How to read this spec

Every fact carries a confidence tag:

- **[S] static**: seen in the firmware's ACPI description by stage A. The plumbing is certain; the meaning may not be.
- **[I] inferred**: meaning guessed from names or usage in firmware, or from a third-party tool's register layout for a different MSI model. Not confirmed on this machine.
- **[H] hypothesis**: a plausible guess that needs a live test before any code may depend on it.
- **[L] live**: confirmed by a reading on Shibela.

Rule for stage C: code may **read** anything tagged [S] that this spec allows. Code may **write** only features marked "write allowed", and only behind the safety rules in section 2. In this first spec nothing is "write allowed" by default; writes are behind an explicit experimental switch.

Names used below:

- "block X instance n" means the WMI data block class X in namespace `root\wmi`, instance number n (0-based, in the order Windows enumerates them).
- "EC 0xNN" means byte NN of the embedded controller's 256-byte register space. "EC 0xNN.b wW" means W bits starting at bit b.
- "word" means 16 bits; byte order within a word is not yet confirmed (see section 6).

## 1. Interface overview

### 1.1 What exists

The laptop exposes one MSI WMI device with ten GUIDs, all plain **data blocks** [S]. There are no WMI methods on it. Every data block instance maps to exactly one EC register field, so:

- **query(block, n)** reads that EC field and returns it.
- **set(block, n, value)** writes `value` into that EC field and returns the field.

| Class (root\wmi) | GUID | Instances | Value type |
|---|---|---|---|
| MSI_Software | 24418D6A-0A79-524C-9AB1-18B78CA68CE7 | 34 | unsigned 8-bit |
| MSI_Device | 4AFBD56B-9F91-8F49-81F5-995BA73822AF | 4 | unsigned 8-bit |
| MSI_Power | 2D3CBA6C-1C9C-7F41-B54C-F5D5D580D482 | 3 | unsigned 8-bit |
| MSI_Master_Battery | 40BA026D-075D-CD4A-9710-F7C57347CAC9 | 16 | signed 16-bit |
| MSI_Slave_Battery | 8DBCCF6E-9DB4-0E46-A3F2-99AFAAA77A7A | 14 | signed 16-bit |
| MSI_CPU | BD2A216F-2FB9-A640-B807-DDDBAD656891 | 19 | unsigned 8-bit |
| MSI_VGA | 1EC3EC7A-1E9B-E74A-9026-CF122B0BBD21 | 18 | unsigned 8-bit |
| MSI_System | A1753D7B-B621-DE4A-B41A-55716A0ECE7A | 21 | unsigned 8-bit |
| MSI_AP | A1753D7C-B621-DE4A-B41A-55716A0ECE7A | 8 | unsigned 8-bit |
| MSI_Event (event) | 5B3CC38A-40D9-7245-8AE6-1145B751BE3F | 1 | unsigned 32-bit payload, 2 meaningful bytes |

All data blocks are flagged "expensive" [S]: Windows only collects them after a consumer enables collection. Stage C must open the block, which enables collection, before querying, and close it when done.

### 1.2 What does not exist (do not depend on it)

- **MSI_ACPI** (GUID ABBC0F6E-…): the class shows up in `root\wmi` because MSI software installs a description of it, but no firmware device backs it on this machine [S]. Calls fail with "not supported". H-Helper must not use it on this model and must treat "class present but not supported" as normal.
- The class descriptions (MOF) for all MSI_* classes come from MSI's installed software, not from the firmware [S]. **On a machine without MSI Center, the named classes may be missing even though the GUIDs work.** Stage C must therefore address blocks by **GUID** (the WMI data-block API with a GUID and instance index), not by class name. Class names are for logs and the UI only.
- The AMD tuning device (GUID ABBC0F6A-…, AMD's overclocking interface) is out of scope. H-Helper must never call it.

### 1.3 Permissions

A normal user gets "access denied" when reading the MSI blocks [S, observed by stage A]. Whether an elevated administrator is enough, or whether SYSTEM is needed, is open (section 6, Q2).
Design consequence: the hardware access lives in a small privileged component (a service or an elevated helper). The UI talks to it through a narrow, typed request list (section 5.1), never through "read/write any instance". This also lets the allowlist in section 2 be enforced in one place.

## 2. Safety rules (mandatory for stage C)

1. **Allowlist, not enumeration.** The privileged component holds a fixed table of (GUID, instance, allowed operations). Any request for something not in the table is refused. Never loop over "all instances" of a block, not even for a debug dump, because of rule 2.
2. **Never query MSI_AP instance 1.** Reading it makes the firmware write a fixed value (0x14) into EC 0xCE before returning it [S]. A "read" therefore changes EC state. It is excluded from the allowlist for both read and write.
3. **Never read the MSI_Event payload by polling.** Fetching the event data clears the event code register EC 0xEE [S]. Only read it as the response to an event notification (section 3.8). Likewise do not poll MSI_Software instance 2 (also EC 0xEE): reading it is harmless, but treating it as state invites races with the event path. Exclude it.
4. **Writes are raw.** A set stores the value into the EC with no checking by the firmware [S]. All range and consistency checks are H-Helper's job.
5. **Whole-byte instances that hold flags.** Some instances map to a full byte that the firmware also treats as a set of individual flag bits (MSI_Power 0 at EC 0x30, MSI_Device 0 at EC 0x2E). Writing them would overwrite every flag. These are **read-only, always**, in this spec.
6. **Snapshot before write.** Before the first write of any experimental feature, read and store every register that feature can touch. Offer "restore factory values" that writes the snapshot back. Keep the snapshot on disk so it survives a crash.
7. **Write, then verify.** After a write, read the same instance back. If the value differs, stop the feature, restore the snapshot, and report.
8. **Experimental switch.** Writes are disabled unless the user turns on an "experimental controls" setting that names the model. Off by default.
9. **No unknown writes.** Any instance whose meaning is tagged [H] or "unknown" below is never written, even with the switch on.
10. **Model gate.** All of this applies to MS-158L only. The component checks the system's board/model identifier on start and refuses to enable any MSI table it does not have a spec for.

## 3. Feature catalogue

Each feature lists: what it does, interface, value meaning, read/write status, caveats.

### 3.1 AC adapter present

- Interface: MSI_Power instance 0 → EC 0x30 (whole byte) [S].
- Meaning: bit 0 = AC adapter connected [I, firmware uses this bit to answer the OS "on AC power" question]. Bit 1 and bit 2 are two other status flags whose meaning is not confirmed (possibly lid and keyboard-related) [H].
- Status: read-only. Show only bit 0. Prefer the standard Windows power status API for the UI and use this only as a cross-check.

### 3.2 Battery telemetry (main battery)

- Interface: MSI_Master_Battery instances 0 to 15 [S].
- Value meaning (meanings [I] from firmware register naming; units [H]):

| Inst | EC | Width | Meaning |
|---|---|---|---|
| 0 | 0x31 | 8 | status flags (charging/discharging/full etc.; bit layout not confirmed) |
| 1 | 0x38 | 16 | design capacity |
| 2 | 0x3A | 16 | design voltage |
| 3 | 0x3C | 16 | unknown (capacity-related) |
| 4 | 0x3E | 16 | unknown (status-related) |
| 5 | 0x40 | 16 | cycle count (uncertain) |
| 6 | 0x42 | 16 | unknown |
| 7 | 0x44 | 16 | full charge capacity |
| 8 | 0x46 | 16 | present current (signed: negative likely = discharging) |
| 9 | 0x48 | 16 | remaining capacity |
| 10 | 0x4A | 16 | present voltage |
| 11 | 0x4C | 16 | temperature |
| 12 | 0xE8 | 8 | **unknown, suspicious**: another MSI model uses this byte for the Win/Fn key swap. Do not read into the battery UI; see 3.9 |
| 13 | 0x35 | 8 | unknown |
| 14 | 0x36 | 8 | unknown |
| 15 | 0x4E | 16 | unknown |

- Units: probably mAh, mV, mA; temperature unit unknown (could be 0.1 K or °C) [H]. Stage C must not label units until section 6 Q5 is answered. Until then show raw numbers in a diagnostics view only.
- Status: read-only. Health = full charge ÷ design capacity is safe to compute once units match.
- Caveat: the standard Windows battery API already gives most of this. Use it for the main UI. This block is for extras (cycle count, temperature) once confirmed.

### 3.3 Battery telemetry (second battery)

- Interface: MSI_Slave_Battery instances 0 to 13, same layout as 3.2 shifted to EC 0x32 (status) and 0x50 to 0x65 (words), instance 12 at EC 0xE9, instance 13 at EC 0x66 [S].
- This laptop has one battery. Treat the second battery as "not present" unless its status byte says otherwise [H]. Hide in UI by default.
- Status: read-only.

### 3.4 CPU temperature and CPU fan telemetry

- Interface: MSI_CPU [S]. Meanings [I], partly from the firmware's own naming, partly from a third-party layout for a related model.

| Inst | EC | Meaning | Status |
|---|---|---|---|
| 0 | 0x33 | shared with MSI_VGA 0; unknown | do not use |
| 1 | 0x68 | CPU temperature, °C [I] | read |
| 2 | 0x71 | current CPU fan speed, percent of a fan-table scale [H] | read |
| 3 | 0x70 | unknown | do not use |
| 4 | 0x69 | unknown | do not use |
| 5–10 | 0x6A–0x6F | CPU fan curve: six temperature thresholds (°C) where the fan steps **up** [H] | read; write experimental (3.6) |
| 11–17 | 0x72–0x78 | CPU fan curve: seven fan speed points, one per step [H] | read; write experimental (3.6) |
| 18 | 0x79 | unknown | do not use |

### 3.5 GPU (or system) temperature and GPU fan telemetry

- Interface: MSI_VGA [S]. Same shape as 3.4, shifted:

| Inst | EC | Meaning | Status |
|---|---|---|---|
| 0 | 0x33 | shared with MSI_CPU 0; unknown | do not use |
| 1 | 0x80 | GPU temperature [I]; firmware naming hints "system temperature" instead, so label it "GPU/system temp" until confirmed | read |
| 2 | 0x89 | current GPU fan speed [H] | read |
| 3 | 0x88 | unknown | do not use |
| 4 | 0x81 | unknown | do not use |
| 5–10 | 0x82–0x87 | GPU fan curve: six up-thresholds [H] | read; write experimental |
| 11–17 | 0x8A–0x90 | GPU fan curve: seven speed points [H] | read; write experimental |

### 3.6 Custom fan curves (experimental)

- What: replace the CPU or GPU fan table (6 thresholds + 7 speeds each) with a user curve.
- Interface: MSI_CPU 5–17 and MSI_VGA 5–17 (section 3.4, 3.5).
- Known gaps, which is why this is experimental:
  - The matching **down**-thresholds (hysteresis, EC 0x7A–0x7F and 0x92–0x97 on the related model) are **not** exposed by WMI [I]. Changing only the up-thresholds may make the fan oscillate or never step down cleanly.
  - The **fan mode** register (auto/silent/basic/advanced on the related model, EC 0xD4) is not exposed [I]. The EC may ignore the table unless an "advanced" fan mode is active, so writes may have no visible effect.
- Validation rules for a new curve:
  - thresholds: strictly increasing, within the factory table's own min/max range plus a small margin [H];
  - speeds: non-decreasing, never below the factory minimum, never above the factory maximum (read from the snapshot, not hard-coded);
  - the last speed point must be at least the factory last speed point (never weaken full-load cooling).
- Write order: write speeds first, then thresholds, each followed by read-back verify. On any mismatch restore the snapshot.
- On exit, crash recovery or uninstall: restore the factory snapshot unless the user chose "keep curve".
- Status: disabled until section 6 Q1/Q3 are answered with live reads; then experimental only.

### 3.7 Fan speed in RPM

- Interface: MSI_AP instances, two bytes per fan [S]:
  - CPU fan: instance 7 (EC 0xC8) and instance 6 (EC 0xC9), together a 16-bit value [I];
  - GPU fan: instance 5 (EC 0xCA) and instance 4 (EC 0xCB) [I].
- Meaning: raw tachometer value. On the related model the high byte comes first [I]. The conversion from this raw value to RPM is **not known** yet [H]; do not invent one. Show raw values in diagnostics until a live comparison (fan audibly off vs. full) gives the conversion.
- Read the two bytes of a pair back to back; a value can tear if the EC updates between reads. Read the pair twice and accept only if both reads match.
- Status: read-only. MSI_AP instances 0, 2, 3 (EC 0xCF, 0xCD, 0xCC) unknown: do not use. Instance 1: forbidden (rule 2).

### 3.8 Hotkey and state-change events

- Interface: WMI event MSI_Event (GUID 5B3CC38A-…). Subscribe to it as a WMI event consumer [S].
- Behaviour: when the EC raises its MSI event, the firmware notifies the OS. The event payload has two meaningful bytes [S]:
  - byte 0 = event code (EC 0xEE);
  - byte 1 = an argument that depends on the code:

| Event code(s) | Argument comes from | Likely meaning [H] |
|---|---|---|
| 0x02, 0x08, 0x73, 0x79 | EC 0xE4 bits 3–7 (= MSI_System 1) | a mode selector changed |
| 0x60 | EC 0xE4 bits 0–2 (= MSI_System 0) | another mode selector changed |
| 0x56, 0x57, 0x5F, 0x7B, 0x84 | EC 0x2E (= MSI_Device 0) | a device/feature flag byte changed |
| 0x62, 0x63 | EC 0xF1 (= MSI_System 4) | brightness up / down |
| anything else | argument is 0 | unknown |

  - Fetching the payload clears the event code, so each notification must be read exactly once (rule 3).
- Handling rule: treat every event as "something changed, re-read the related allowlisted instances and refresh the UI". Log unknown codes with their argument, so later stage A runs can assign meanings by pressing each Fn key.
- Status: read (subscribe). No writes.

### 3.9 Mode selectors and flag bytes (read-only until mapped)

- MSI_System 0 → EC 0xE4 bits 0–2 and MSI_System 1 → EC 0xE4 bits 3–7 [S]. The firmware names a "turbo" bit at EC 0xE4 bit 1 [I], so instance 0 likely carries a performance / turbo state [H]. Read and display as raw "mode A" / "mode B" in diagnostics; map to names only after live observation (toggle each mode in MSI Center or with the Fn keys, record values).
- MSI_Device 0 → EC 0x2E (whole byte). Contains a bit the firmware calls GPU-switch related (bit 6) [I]. **Read-only, permanently**, until GPU switching gets its own spec (rule 5).
- MSI_Device 1–3 → EC 0x2F, 0x2B, 0x2C: unknown, do not use.
- MSI_Master_Battery 12 → EC 0xE8: possible Win/Fn key swap (value 0x10 on a related model) [H]. Candidate future feature "swap Win and Fn"; read-only until a live toggle confirms it.

### 3.10 Display brightness level

- Interface: MSI_System 4 → EC 0xF1 [S]; firmware naming says "brightness level" [I]. MSI_System 5 and 6 (EC 0xFE, 0xFF) may be the stored DC and AC brightness levels [H].
- Status: read-only. Use the standard Windows brightness interface for changing brightness. This value is only useful to explain brightness hotkey events.

### 3.11 EC firmware information

- Interface: MSI_Software instances 6 to 33 → EC 0xA0 to 0xBB, 28 consecutive bytes [S].
- Meaning: plausibly an ASCII EC firmware version and/or date [H].
- Behaviour: read all 28 bytes once at startup; if every byte is printable ASCII or zero, show it as "EC firmware" in the About page, else show hex. Never write.
- Use for the model gate (rule 10): log it with the board ID so user reports can be matched to EC versions.

### 3.12 Everything else (do not use)

These instances are plumbed [S] but have no known meaning. They are excluded from the allowlist:
MSI_Software 0, 1, 3 (bit fields of EC 0x2D), 2 (EC 0xEE, event code; rule 3), 4 (EC 0xE5), 5 (EC 0xE6, firmware calls it debug [I]: never touch);
MSI_Power 1, 2 (EC 0xBC, 0xBD, OS-related flags [I]);
MSI_System 2, 3, 7–18, 19, 20 (EC 0xE7, 0xF0, 0xF2–0xFD, 0xE0, 0xE3);
MSI_CPU 0, 3, 4, 18; MSI_VGA 0, 3, 4; MSI_AP 0, 2, 3; MSI_Device 1–3.

## 4. Out of scope for the first release

### 4.1 Features only reachable by raw EC access

A related MSI model keeps these at EC offsets that **no WMI instance on Shibela exposes** [I]:
Cooler Boost (EC 0x98 bit 7), performance mode (EC 0xD2), fan mode (EC 0xD4), battery charge limit (EC 0xD7), fan down-thresholds.
Reaching them needs a kernel driver doing raw EC port access. That brings back the security problem H-Helper exists to remove. Decision for stage C: **not implemented**. Revisit only with a separate design decision (for example a minimal, signed, allowlisting driver), and only after a read-only check that the offsets even match on this model.

### 4.2 GPU switching

No MSI firmware interface for GPU or MUX switching exists on this model [S]. Switching is AMD's standard hybrid graphics. The only firmware knob is AMD's hidden setup variable (Special Display Features: hybrid or disabled; primary adapter: integrated or external), currently at the stock values hybrid + integrated [L, from the AMD_PBS_SETUP read]. The owner's earlier tests of those options gave a black screen or no change, which suggests the internal panel is wired to the iGPU only (no MUX) [H].
Decision for stage C: **no GPU switching feature, and no code that writes UEFI variables.** At most a read-only "Graphics: hybrid (stock)" line in diagnostics, read from that variable (AMD_PBS_SETUP, GUID A339D746-F678-49B3-9FC7-54CE0F9DF226) by the privileged component: data offset 0x04 = 4 means hybrid, 0 means disabled; offset 0x06 = 1 means integrated primary, 2 means external [L].

## 5. Pseudocode

### 5.1 Privileged component: request surface

```
ALLOWLIST = table of entries:
    { feature, block_guid, instance, value_kind, can_read, can_write_experimental }
    built from section 3; any (guid, instance) not listed is refused.
    MSI_AP instance 1 and MSI_Software instance 2 are never listed.

on start:
    if board_id != "MS-158L": disable MSI table, report "unsupported model", stop
    for each block used: open block (enables collection)
    ec_info = read_ec_info()            # section 3.11
    load snapshot file if present        # crash recovery, see 5.4

requests the UI may send (nothing else):
    GetTelemetry()               -> temps, fan speeds, raw fan tach, AC flag
    GetBatteryExtras()           -> raw battery instances (diagnostics)
    GetDiagnostics()             -> ec_info, mode A/B raw, graphics mode
    GetFanCurve(fan)             -> 6 thresholds + 7 speeds
    SetFanCurve(fan, curve)      -> only if experimental switch on (5.4)
    RestoreFactory()             -> writes snapshot back
    SubscribeEvents()            -> stream of (code, arg)

function read_instance(guid, n):
    entry = ALLOWLIST.find(guid, n)
    if not entry or not entry.can_read: refuse
    return wmi_query_data_block(guid, n)     # by GUID, not class name

function write_instance(guid, n, value):
    entry = ALLOWLIST.find(guid, n)
    if not entry or not entry.can_write_experimental: refuse
    if not settings.experimental_enabled: refuse
    wmi_set_data_block(guid, n, value)
    if read_instance(guid, n) != value: raise WriteVerifyFailed
```

### 5.2 Telemetry poll

```
every 2 seconds while a UI is attached (else every 30 s or not at all):
    cpu_temp  = read MSI_CPU 1
    gpu_temp  = read MSI_VGA 1
    cpu_fan_% = read MSI_CPU 2
    gpu_fan_% = read MSI_VGA 2
    cpu_tach  = read_pair(MSI_AP 7 as high, MSI_AP 6 as low)
    gpu_tach  = read_pair(MSI_AP 5 as high, MSI_AP 4 as low)
    on_ac     = (read MSI_Power 0) bit 0
    publish

function read_pair(high_inst, low_inst):
    repeat up to 3 times:
        a = (read high << 8) | read low
        b = (read high << 8) | read low
        if a == b: return a
    return "unstable"
```

Note: every query is a firmware round trip into the EC. Keep the poll set small and the rate low; never poll from more than one thread.

### 5.3 Events

```
subscribe to WMI event GUID 5B3CC38A-...:
    on event(payload):
        code = payload byte 0
        arg  = payload byte 1
        log(code, arg)
        if code in {0x62, 0x63}: refresh brightness display
        else if code in {0x02, 0x08, 0x73, 0x79, 0x60}: re-read MSI_System 0 and 1
        else if code in {0x56, 0x57, 0x5F, 0x7B, 0x84}: re-read MSI_Device 0
        else: record as unknown
    never fetch the payload outside this handler
```

### 5.4 Experimental fan curve write

```
function SetFanCurve(fan, new_curve):
    require experimental switch on
    ids = instances 5..17 of MSI_CPU or MSI_VGA for this fan
    if no snapshot for this fan on disk:
        snapshot = read all ids; save to disk; flush
    factory = snapshot for this fan
    validate(new_curve, factory) or refuse with reason      # rules in 3.6
    try:
        for each speed point i:   write_instance(speed id i, new speed i)
        for each threshold i:     write_instance(threshold id i, new threshold i)
    on any failure:
        RestoreFactory(fan); report failure

function RestoreFactory(fan):
    for each id in snapshot (speeds first, then thresholds): write saved value
    verify each; if all match, delete snapshot for that fan
```

## 6. Open questions to send back to stage A (read-only live checks)

1. **Q1 fan table layout.** Read MSI_CPU 1–17 and MSI_VGA 1–17 at idle and under load. Do 5–10 look like rising temperatures and 11–17 like rising speeds? Does 2 track load?
2. **Q2 permissions.** Can an elevated administrator query the blocks, or is SYSTEM needed? Which account does the block security allow?
3. **Q3 does the table take effect?** Only if the owner agrees: change one speed point slightly, observe fan, restore. Tells whether a fan mode outside WMI gates it.
4. **Q4 tach conversion and byte order.** Read MSI_AP 4–7 (never 1) with fans stopped and at full speed (e.g. under load) and compare with an independent RPM reading if any exists.
5. **Q5 battery units.** Compare MSI_Master_Battery 1, 7, 8, 9, 10, 11 with the Windows battery report for the same moment.
6. **Q6 event codes.** Press each Fn hotkey and switch each mode in MSI Center; record (code, argument) pairs and MSI_System 0/1 values.
7. **Q7 EC info string.** Read MSI_Software 6–33 once; is it printable text?
8. **Q8 Win/Fn swap.** Toggle the Win/Fn swap in MSI Center; does EC 0xE8 (MSI_Master_Battery 12) change?
9. **Q9 second battery.** Value of MSI_Slave_Battery 0 on this single-battery machine.

## 7. Provenance

- Interface facts: stage A static reading of this machine's own ACPI tables (no execution, no writes).
- Related-model register layout: stage A's notes on facts (offsets and meanings only) from a third-party GPL-3.0 tool's configuration for a different MSI model. No code or text from it is used here. Everything derived from it is tagged [I] or [H].
- Graphics setup values: owner-run read-only dump of the AMD setup variable.
