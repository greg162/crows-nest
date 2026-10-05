# Crowsnest

A COM radio panel for Microsoft Flight Simulator, built on the Elecrow CrowPanel
2.1" ESP32 Rotary Display.

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

## Build

```
dotnet build host/Crowsnest.slnx
dotnet test  host/Crowsnest.slnx
```

.NET 10 emits the XML solution format (`.slnx`) rather than `.sln`.

## Layout

```
host/                      the Windows side: Crowsnest.slnx, Directory.Build.props
  src/
    Crowsnest.Core/          net10.0          BCL only — no project references
    Crowsnest.SimConnect/    net10.0-windows  no project references (NuGet candidate)
    Crowsnest.Sim/           net10.0-windows  -> Core, SimConnect
    Crowsnest.Device/        net10.0-windows  -> Core
    Crowsnest.Host/          net10.0-windows  -> Core, Sim, Device
  tools/
    Crowsnest.DevConsole/         runs the host headless, until the tray app exists
    Crowsnest.DeviceSimulator/    the host against a fake sim and a simulated device
  tests/
    Crowsnest.Core.Tests/    mirrors the Core folder structure exactly
    Crowsnest.Device.Tests/
    Crowsnest.Sim.Tests/
    Crowsnest.Host.Tests/    settings file, and the bridge end to end on simulated USB
firmware/
  crowsnest-display/       ESP-IDF 6.1 + LVGL 9
```

**The dependency rule.** `Core` references nothing but the BCL. Adapters (`Sim`,
`Device`) reference `Core` and never each other. `Host` composes. Inside `Core`,
`Domain/` and `Application/` must never reference `Panels/`, and no panel may
reference another panel. An architecture test enforces this (§11) — it is the
rule that erodes quietly under deadline pressure.

`Crowsnest.SimConnect` deliberately has no reference to `Core`.

### Not yet created

`Crowsnest.Tray` and `Crowsnest.Installer` are Phase 4 and deliberately absent.
The WiX v5 template pack is not installed (`dotnet new install WixToolset.Templates`).

## Status

Phases 0–3 are done: the host drives real CrowPanels against MSFS 2024 over SimConnect.
COM 1, COM 2 and NAV 1 ship as panel modules (`host/src/Crowsnest.Core/Panels/`, see its
README for adding one), and each board shows the panels assigned to it in
`%LOCALAPPDATA%\Crowsnest\settings.json`. Next: the tray app and installer (phase 4).
See `crowsnest-technical-spec.md` §13 for the phases.
