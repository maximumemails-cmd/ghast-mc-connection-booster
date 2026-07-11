# Ghast — MC Connection Booster

A Windows network-tweaking tool for Minecraft players. It applies real Windows
optimisations (registry values, `netsh`, QoS DSCP policies, process priority,
adapter power settings), saves them as presets, and can **fully revert** every
change it made.

- .NET 8 · WPF · MVVM (`CommunityToolkit.Mvvm`) · no external UI frameworks
- Requires **Administrator** (everything touches HKLM / netsh / adapters)
- Data lives at `%AppData%\Ghast\` (`config.json`, `backup.json`, `presets\*.ghast`, `log.txt`)

## Three honest truths (also in the tooltips)

1. **Software cannot lower real latency.** Ping is distance + routing. Ghast only
   removes delay that *Windows itself* adds (Nagle bundling, delayed ACKs,
   background throttling).
2. **Several tweaks are debatable** and can hurt some setups (e.g. disabling
   delayed ACKs on lossy links). That's exactly why Restore Defaults exists.
3. **Everything is reversible.** Before Ghast writes any value it captures the
   original into `backup.json`; the first capture is never overwritten, so
   *Restore Defaults* always returns the machine to its true pre-Ghast state.

## Building

```bash
dotnet publish -c Release -r win-x64 \
  -p:PublishSingleFile=true \
  --self-contained true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

Output: a single `Ghast.exe` that prompts for UAC on launch. The project sets
`EnableWindowsTargeting=true`, so it also compiles on macOS/Linux dev machines
(it only *runs* on Windows).

## Installer

`installer/Ghast.iss` (repo root) builds a proper setup exe with
[Inno Setup 6](https://jrsoftware.org/isinfo.php). It installs to Program Files,
creates Start Menu + optional desktop shortcuts, registers in Apps & Features with
the Ghast icon, and offers "Launch Ghast now". Nothing is downloaded; it only
bundles the published exe.

**Upgrades replace the old version and keep user data**: the `[Setup]` block uses a
fixed `AppId` GUID (never change it), and the `[Code]` section backs up
`%AppData%\Ghast` (settings, presets, and `backup.json` with the pre-Ghast values),
silently uninstalls any previous install, installs clean, then restores the folder.
Uninstall removes program files and the startup Run key but never touches
`%AppData%\Ghast`.

The installer builds in CI (`.github/workflows/build-installer.yml`, windows-latest):
pushing a `v*` tag builds `Ghast-Setup.exe` and attaches it to a GitHub **Release**;
`workflow_dispatch` builds it as a run artifact on demand. To build locally on a
Windows machine instead:

```bat
dotnet publish Ghast/Ghast.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\Ghast.iss
```

Output: `installer\Output\Ghast-Setup.exe`.

## First run & app options

On the first launch after install, a one-time welcome dialog offers two toggles
(both default OFF, both repeatable later from the bottom of the Settings tab):

- **Start Ghast when Windows starts** — a per-user `HKCU\...\Run` entry. Honest
  caveat (also in the tooltip): Ghast's manifest demands administrator, and some
  Windows builds *silently skip* elevated apps at logon instead of prompting. If
  autostart doesn't fire on your machine, that's Windows policy — a scheduled task
  with highest privileges is the workaround if you ever want it.
- **Pin Ghast to the taskbar** — best effort. Windows deliberately restricts
  self-pinning, so Ghast uses Explorer's own pin handler (read from the registry
  CommandStore at runtime) and *verifies* the result; if the build blocks it, the
  toggle flips back and the status strip explains how to pin manually.

The "first run done" flag lives in `config.json`, so the dialog never nags again.

## Ping tab (connection tester)

Real [Server List Ping](https://wiki.vg/Server_List_Ping) protocol: SRV lookup
(`_minecraft._tcp.<host>`, via the **DnsClient** NuGet package) with fallback to
the A record + port 25565, then TCP handshake (next state = 1), status request
(MOTD, player count, version), and timed ping/pong rounds across ~8 seconds.
Vanilla servers close after one pong, so each sample is its own short connection —
the per-connection failure rate is what "failed pings" reports. Results: avg/min/max
latency, jitter, loss, a 0–100 score with a letter grade, and plain-English ratings.
The UI labels it honestly: it measures **your route to the server's edge**, not the
server's internal proxy/routing.

## Fullscreen scaling

Body content is designed against 1280×800 and scales up via a `LayoutTransform`
`ScaleTransform` recomputed on `SizeChanged`:
`scale = clamp(min(w/1280, h/800), 1.0, 1.8)`. LayoutTransform re-renders text at
the target size (crisp, no Viewbox blur), the cap keeps type from ballooning, and
the floor means small windows just scroll. Maximized at 1080p ≈ 1.35×, at 1440p
the 1.8× cap applies.

## What each control really does

| Control | Mechanism |
|---|---|
| Smart Packets | `TcpAckFrequency=1`, `TCPNoDelay=1` per active interface GUID (OFF deletes them) |
| Latency / Packets Delay | `TcpDelAckTicks` per interface — **merged control, see below** |
| Responsiveness | `SystemResponsiveness` (written value = `20 − slider`) |
| Tuning | `netsh int tcp set global autotuninglevel=…` |
| Type | Logic only — seeds sensible defaults for the other controls |
| Connection Stable OFF | Clamps on Run: autotuning→Normal, delayed ACKs kept, congestion/MTU changes skipped |
| Competitive Mode | Bundle: Smart Packets ON, Responsiveness max, `NetworkThrottlingIndex=0xFFFFFFFF`, Priority Mode ON, Power Saving handled ON — snapshots prior state so OFF restores it |
| MTU | `netsh interface ipv4 set subinterface … mtu=… store=persistent` (Automatic = leave alone / restore original) |
| Network Priority | QoS DSCP policy (level 1–5 → DSCP 10/18/26/34/46) + game process priority (Normal/AboveNormal/High) |
| Congestion Provider | `netsh int tcp set supplemental template=internet congestionprovider=…` (pre-1709 fallback: `set global congestionprovider=`, CTCP/DCTCP only) |
| Ghast Priority Mode | Minecraft process → High (never RealTime) + `Tasks\Games` multimedia keys (`Priority=6`, `Scheduling Category=High`, `SFIO Priority=High`) |
| Network Power Saving ON | `PnPCapabilities=24` on the adapter's driver class key + `*EEE=0` where present (ON = power saving *disabled*) |
| DNS (gear menu) | `netsh interface ip set dns … static` Cloudflare/Google; original config backed up, restore returns to DHCP/previous static |

### The Latency ↔ Packets Delay merge

Both sliders control the same Windows value (`TcpDelAckTicks`), so they are
**merged**: *Packets Delay* (Advanced, 0–6) is authoritative and maps to
`ticks = 6 − slider`; *Latency* (Settings, 0–4) mirrors it
(`0→2 ticks default, 1→1, 2..4→0`). Moving either slider updates the other, and
only one registry write happens on Run. Because the Latency mapping is coarser,
mid-range Packets Delay values (ticks 3–6) all display as Latency 0 — that is
lossy by design and only affects the mirror, never the written value.

### Deviations from the build spec (flagged deliberately)

- **Two QoS policies instead of one** (`Ghast-Minecraft-javaw`, `Ghast-Minecraft-java`):
  Windows policy-based QoS matches a single `Application Name` per policy, and the
  spec asks to cover both `javaw.exe` and `java.exe`.
- **`ConfigService`** was added (config.json load/save helper) — infrastructure,
  not a feature.
- **`competitiveSnapshot`** is an extra field in the config schema so the
  Competitive Mode OFF-restore survives an app restart.
- **Preset order** persists in `presets\_order.json` (spec asks for drag
  reordering but doesn't say where to store it).
- **`firstRunDone`** is an extra config field backing the one-time welcome dialog.
- **`pingTarget`** is an extra config field (host or host:port) backing per-server
  profiles — saved with the config and inside every preset.
- **Presets carry a `version` field** (`1`) so shared `.ghast` files can be
  migrated in future; files without it import as v1.
- **`DnsClient` NuGet package** was added for SRV record resolution on the Ping
  tab (the only non-UI dependency besides `CommunityToolkit.Mvvm`).
- Smart Packets OFF *deletes* `TcpAckFrequency`/`TCPNoDelay` per the spec — but the
  pre-existing values are backed up first, so Restore Defaults still returns
  whatever was there before Ghast.

### Safety model

- `app.manifest` demands `requireAdministrator`; a runtime check self-relaunches
  elevated if a launcher ignored the manifest.
- Every write path goes through `BackupService`: read current → record
  `BackupEntry { type, path, name, originalValue, existedBefore }` → write.
  Entries are keyed; the first capture wins forever.
- **Restore Defaults** (gear menu) walks every entry: `existedBefore=false` →
  delete the value/policy (and the parent key too if Ghast created it and it is
  empty again); otherwise write the original back. netsh restore commands are
  verified by exit code, and the backup store is cleared only if *every* restore
  succeeded — a failed restore keeps `backup.json` so it can be retried.
- Confirmation dialogs list the planned changes before Run and before Restore.
- Process priority is capped at High; RealTime is never used.
- Every registry/netsh call is wrapped: failures land in the status strip and
  `%AppData%\Ghast\log.txt`, and the rest of the run continues.
- `AdapterService.FlushAsync` (flushdns + adapter bounce) still exists but is no
  longer surfaced: the follow-up Run/Stop flow replaced the old post-Run flush
  prompt. Wire it to a gear-menu button if you want it back.

## Run / Stop flow

The footer button is a stateful toggle driven by `MainViewModel.RunState`
(`Idle → Starting → Running → Stopping`):

- **Run** (Idle) opens a modal progress popup (`RunProgressWindow`) with a red
  progress bar and live step text driven by real `ApplyService` progress
  (`IProgress<ApplyProgress>`), held open ≥900 ms so it can't flash. On success it
  flips to **Running** and offers **Stop**/**Close**; on any failed step it rolls
  the whole pass back via `RestoreAllAsync` and offers **Retry**/**Close** so the
  machine is never left half-applied.
- **Reconnect nudge.** The per-interface TCP values (`TcpAckFrequency`,
  `TCPNoDelay`, `TcpDelAckTicks`) are only read by Windows when a TCP connection
  is *established* — hitting Run mid-game does not touch the live Minecraft
  socket. So whenever a Run changed those values, the success popup and the
  status strip both say it plainly: **reconnect to your server (leave and
  rejoin) for the TCP tweaks to take effect on your current session.**
- **Opt-in "Apply to live connection".** The success popup also offers an
  explicit button that flushes DNS and briefly disables/re-enables each adapter
  (~3 s) so already-open connections re-establish with the new settings — behind
  a confirm dialog that names the consequence (everything disconnects). It is
  never automatic and the tooltip says exactly what it is: it does **not** lower
  latency; it only forces the settings onto the live session. (History: v1 had a
  post-Run "flush now?" prompt whose adapter bounce dropped the Minecraft
  session — that disconnect was a side effect that *accidentally* made the TCP
  tweaks apply immediately, not extra optimisation power. This brings the useful
  half back, with consent.)
- **Stop** (Running) reuses the popup to run `RestoreAllAsync`, reverting every
  captured value, then returns to **Idle**.
- **Restore verification.** After every restore pass, Ghast re-reads each
  registry/netsh value and confirms it equals the captured original (or is gone,
  if it didn't exist before). `backup.json` is only cleared when restore *and*
  verification both pass; any mismatch is surfaced as a failed step and the
  backups are kept for retry.
- The footer is disabled during `Starting`/`Stopping` so clicks can't stack
  applies/reverts. On launch, `RunState` starts as **Running** if `backup.json`
  already holds values (tweaks are live from a previous session).
- Closing the window while **Running** prompts *revert / leave applied / cancel*.
  The revert path runs on the thread pool (`Task.Run(...).GetAwaiter().GetResult()`)
  to avoid a sync-over-async UI-thread deadlock.

## Receipts, dry runs and proof

Trust comes from showing the work:

- **Preview (footer button).** A true dry run: computes exactly what Run would
  change with the current settings — live value vs the value that would be
  written, including the unstable-connection clamps — without touching anything.
- **What changed (footer button, visible while Running).** A plain-English
  receipt built from `backup.json`: every value Ghast changed, its captured
  pre-Ghast original (what Stop returns to), and the live value re-read now.
- **Before/after ping proof.** If a server is set on the Ping tab, Run first
  takes a quick 4-sample baseline. After reconnecting, hit **Test** and the Ping
  tab shows the delta vs that baseline — and reports **"no measurable change"**
  when the difference is inside the jitter band, because Ghast removes
  Windows-added delay, it can't shorten the route.
- **Live monitor (Ping tab).** Samples the server every 4 s (real Server List
  Ping probes) and charts latency as a sparkline with avg / jitter / loss over
  the last window. Failed probes count as loss and leave gaps in the line.
- **Per-server profiles.** The Ping-tab server is saved in `config.json`
  (`pingTarget`) and therefore inside every preset — an exported "Hypixel setup"
  recalls both the tweaks and the server.
- **Detect from adapter (Settings tab).** Suggests `Type` from the active
  adapter. Honest limits: wireless vs wired is real; fiber/cable/DSL are classed
  by link speed and labelled as a guess — a NIC can't truly tell them apart.
- **Preset export.** Select Mode → *Export Selected* writes shareable `.ghast`
  files (now carrying a `version` field). Imports are structurally validated and
  sanitized — fields are never trusted blindly.

## Presets: built-ins + Explain

Eight baked-in presets are added alongside the original four demos: *Best Hit-Reg,
Best KB, 1.8.9 Balanced, Modern Balanced, Competitive Max, Stable Wi-Fi, BedWars
Rush, High-Ping Fix*. They are built **only** from existing `GhastConfig` fields
(no invented settings), marked `IsBuiltIn` (protected from deletion, re-seeded if
missing so upgrades gain them), and explained honestly in a **ⓘ Explain** popup on
the Presets tab. Mapping notes (also `// FLAG:` comments in `PresetService`):

- The table's *Latency* (0–4) maps onto the authoritative `PacketsDelay` as
  `PacketsDelay = 2 + latency` (L4 → delayed-ACK off); the Settings "Latency"
  slider is a coarse mirror recomputed on load.
- *NIC Power "OFF"* (low-latency) = `NetworkPowerSaving = true` (the toggle's
  `true` means "adapter power management disabled").
- `ConnectionStable` stays `true` on all built-ins (incl. Stable Wi-Fi) so an
  explicit `Tuning` value isn't clamped to Normal by the unstable-connection guard.
- CTCP rows use `CTCP` (the dropdown already exposes it — no fallback needed).

### Notes

- netsh output parsing (autotuning/congestion/MTU) is locale-tolerant but assumes
  English column layout for MTU; on non-English Windows the current-value capture
  falls back to documented defaults and logs it.
- Test on a throwaway VM first: TCP/registry edits can break connectivity — the
  whole point of `backup.json` is that you can undo.
