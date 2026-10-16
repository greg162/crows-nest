<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="media/logo/png/crowsnest-stacked.png">
    <img src="media/logo/png/crowsnest-stacked-light.png" alt="Crowsnest" width="240">
  </picture>
</p>

# Crowsnest

Cockpit panels for Microsoft Flight Simulator: COM, NAV and transponder radios on Elecrow
CrowPanel 2.1" ESP32 rotary displays, driven by a Windows tray app over SimConnect.

See [`crowsnest-technical-spec.md`](crowsnest-technical-spec.md) for the full design.

## Repository location matters

**This repository must live at a short path — `C:\projects\crowsnest`.**

It is not a preference. Windows caps a full path at 260 characters and the
ESP-IDF build tree is deep (`build/esp-idf/<component>/CMakeFiles/__idf_<component>.dir/...`).
Building `hello_world` from a 118-character path already put object files 199
characters in. The real firmware — LVGL 9 plus a generated BSP component — has
substantially less headroom. See §9.6 F3.

Git is the backup mechanism. Do not move this tree back under OneDrive: it
generates hundreds of megabytes of build output that has no business syncing.

## Prerequisites

| Prerequisite | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.100+ | Pinned in `global.json` with `latestFeature` roll-forward |
| MSFS 2024 SDK | 12.2.0.0 | Supplies native x64 `SimConnect.dll`. Vendor into the repo, do not reference the SDK path |
| ESP-IDF | 6.1 via EIM | Machine-level, not project-scoped. §9.3 |
| USB-UART bridge drivers | — | `eim install-drivers`. Not required for the CrowPanel itself (native USB-Serial/JTAG, §9.6 F4) but needed for other boards and programming adapters |

## Build and run

```
dotnet build host/Crowsnest.slnx
dotnet test  host/Crowsnest.slnx
dotnet run --project host/src/Crowsnest.Tray
```

The tray app and DevConsole share a single-instance guard: run one or the other, never both.

The installer is built on its own, not with the solution. It publishes the tray app with the
.NET runtime included, then packs it into `host/src/Crowsnest.Installer/bin/Release/Crowsnest-<version>.msi`
(about 50 MB). The version comes from `host/Directory.Build.props`.

```
dotnet build host/src/Crowsnest.Installer -c Release
```

It installs for the current user only, with no administrator prompt. The wizard shows the MIT
licence, then asks for a folder (`%LOCALAPPDATA%\Programs\Crowsnest` by default; an upgrade
reuses the last one). It adds a Start menu shortcut and starts the app when it finishes. A newer
MSI upgrades in place. Settings and logs in `%LOCALAPPDATA%\Crowsnest` are
kept on uninstall.

.NET 10 emits the XML solution format (`.slnx`) rather than `.sln`.

Firmware goes on the boards with the flasher: build it (`idf.py build` in
`firmware/crowsnest-display`), then double-click `flasher/Flash-Crowsnest.cmd` and pick a board.
It needs no ESP-IDF of its own, so the same folder, zipped with a firmware image, is what
other people use. See `flasher/README.md`.

## Layout

```
host/                      the Windows side: Crowsnest.slnx, Directory.Build.props
  src/
    Crowsnest.Core/          net10.0          BCL only — no project references
    Crowsnest.SimConnect/    net10.0-windows  no project references (NuGet candidate)
    Crowsnest.Sim/           net10.0-windows  -> Core, SimConnect
    Crowsnest.Device/        net10.0-windows  -> Core
    Crowsnest.Host/          net10.0-windows  -> Core, Sim, Device
    Crowsnest.Tray/          net10.0-windows  -> Host; the app (Crowsnest.exe): tray icon, settings, diagnostics
    Crowsnest.Installer/     WiX v5           the MSI; not in the solution (see Build and run)
  tools/
    Crowsnest.DevConsole/         runs the host headless, with its log in the console
    Crowsnest.DeviceSimulator/    the host against a fake sim and a simulated device
  tests/
    Crowsnest.Core.Tests/    mirrors the Core folder structure exactly
    Crowsnest.Device.Tests/
    Crowsnest.Sim.Tests/
    Crowsnest.Host.Tests/    settings file, and the bridge end to end on simulated USB
    Crowsnest.Tray.Tests/    the settings and diagnostics view models
    protocol-corpus/         malformed wire lines the device parser must survive
firmware/
  crowsnest-display/       ESP-IDF 6.1 + LVGL 9
flasher/                   puts firmware on the boards with esptool (see its README)
media/                     logo, app icon, DM Sans, colours: the brand kit (see its README)
```

**The dependency rule.** `Core` references nothing but the BCL. Adapters (`Sim`,
`Device`) reference `Core` and never each other. `Host` composes. Inside `Core`,
`Domain/` and `Application/` must never reference `Panels/`, and no panel may
reference another panel. An architecture test enforces this (§11) — it is the
rule that erodes quietly under deadline pressure.

`Crowsnest.SimConnect` deliberately has no reference to `Core`.

## Status

Phases 0–3 are done, and phase 4 is under way. The host drives several CrowPanels at once
against MSFS 2024 over SimConnect. Three panels ship as modules in
`host/src/Crowsnest.Core/Panels/` (its README covers adding one):

- **COM**: COM 1 and COM 2, active and standby, tap to swap
- **NAV**: NAV 1 and NAV 2
- **Transponder**: the squawk code, tap for IDENT, with the mode shown under it

Each board shows one panel, chosen in the tray app's settings window and saved to
`%LOCALAPPDATA%\Crowsnest\settings.json`. The tray app also has a diagnostics window: link
stats for each board and the recent log. It ships as a per-user MSI, and the flasher puts
firmware on the boards (spec §6.3). See `crowsnest-technical-spec.md` §13 for the phases.

## Licence

MIT, see `LICENSE`. The installer also ships `SimConnect.dll` and
`Microsoft.FlightSimulator.SimConnect.dll` from the Microsoft Flight Simulator SDK; they belong
to Microsoft and are not covered by the MIT licence.
