# Crowsnest — Technical Specification

**A COM radio panel for Microsoft Flight Simulator built on the Elecrow CrowPanel 2.1" ESP32 Rotary Display**

Version 0.2 — draft for review

**Naming convention.** `Crowsnest` — one word, no apostrophe, PascalCase in code (`Crowsnest.Core`), lowercase in paths and identifiers (`crowsnest-display`, `%LOCALAPPDATA%\Crowsnest\`). Avoid `Crow'sNest` and `CrowsNest`; the apostrophe is hostile to filesystems and the split-capital form invites inconsistent casing across the codebase.

---

## 1. Scope

A Windows application that connects to Microsoft Flight Simulator via SimConnect and drives an Elecrow CrowPanel 2.1" rotary display over a serial link. The knob tunes, the knob click moves the edit cursor between digit groups, and a screen tap swaps active and standby.

The system is designed from the outset as a **general sim panel bridge that ships COM 1 first**, not as a COM 1 tool that will later be generalised. Sections 5 and 6 reflect that: the domain models arbitrary tunable parameters, and the wire protocol carries formatted text rather than frequencies.

**v1 — shipped feature set**

- COM 1 active + standby, read and write
- Rotary tuning with a multi-level cursor, 25 kHz and 8.33 kHz channel spacing
- Active/standby swap
- Windows installer producing an Add/Remove Programs entry
- System tray application with settings and diagnostics
- USB serial transport over a powered hub, **multiple panels supported from v1**
- Firmware updates pushed over the same link
- Custom firmware for the CrowPanel

**Planned extensions, accommodated by the v1 architecture**

| Phase | Feature | Cost in the current design |
|---|---|---|
| 5 | NAV 1 + NAV 2 | one `Panels/Nav/` module, one catalogue line |
| 5 | COM 2 | two JSON entries and two pages in the existing `Panels/Com/` module |
| 6 | Autopilot: altitude, heading, vertical speed, airspeed | one `Panels/Autopilot/` module |
| 6 | Transponder, OBS, barometer | one module each; transponder needs a custom `IPanelBehaviour` |
| 7 | Additional rotary boards (M5Dial, Viewe, Waveshare) | 1 BSP + 1 input shim + 1 sdkconfig per board |
| 7 | Wi-Fi transport, for panels mounted away from the PC | 1 `IDeviceTransport` implementation |

**Out of scope indefinitely**

- Bluetooth transport — explicitly rejected, see D2
- Non-rotary hardware (button boxes, multi-encoder panels)
- Aircraft-specific L-var support for study-level add-ons — the registry has a slot for it (§5.3) but the WASM plumbing is a separate project
- Anything requiring a SimConnect connection over a network

## 2. Assumptions and open decisions

These need confirming before build starts. Where a decision is needed now, the default is stated.

| # | Item | Default assumption |
|---|---|---|
| A1 | **What the knob edits.** The brief says the dial scrolls "the main frequency" but also that a tap swaps active/standby. Real-world and sim convention is to tune standby, then swap it into active. | **The knob edits standby.** The standby readout is rendered as the large primary figure while editing. Expose `EditSlot` as a setting so it can be flipped to Active. |
| A2 | Target simulator | MSFS 2024, with MSFS 2020 as a compatibility target. Both use the same COM SimVars and key events. |
| A3 | Long-press behaviour | Reserved for cycling COM 1 ↔ COM 2 in a later phase. v1 treats it as short press. |
| A4 | Runs on the same PC as the sim | Yes. SimConnect over a network is possible via `SimConnect.cfg` but is out of scope. |
| A5 | Deployment model | Per-user tray application, autostarted. Not a Windows service — a service gains nothing here and complicates SimConnect and the UI. |
| A6 | .NET version | .NET 10 (LTS), `win-x64` only. SimConnect's native DLL is x64. |

---

## 3. Architecture

Three tiers with a strict dependency direction. The PC owns all state and all logic; the firmware is a rendering surface and an input source.

```
┌──────────────────────────────────────────────────────────────────┐
│  MSFS 2020 / 2024                                                │
│  SimConnect server (in-process)                                  │
└───────────────────────┬──────────────────────────────────────────┘
                        │ SimConnect.dll (native, x64)
┌───────────────────────▼──────────────────────────────────────────┐
│  Crowsnest (Windows, .NET 10)                                    │
│                                                                  │
│   Crowsnest.SimConnect ──► Crowsnest.Sim ──┐                     │
│   (P/Invoke library)       (gateway)       │                     │
│                                            ▼                     │
│                                    Crowsnest.Core                │
│                            (parameter registry · value grids ·   │
│                             tuning state · PanelCoordinator)     │
│                                            ▲                     │
│                             Crowsnest.Device┘                    │
│                             (transport + NDJSON codec)           │
│                                                                  │
│   Crowsnest.Host (generic host, DI, config, logging)             │
│   Crowsnest.Tray (WPF tray icon + settings)                      │
└───────────────────────┬──────────────────────────────────────────┘
                        │ powered USB hub · NDJSON · formatted text
┌───────────────────────▼──────────────────────────────────────────┐
│  1..N rotary panels  (ESP32-S3), addressed by eFuse MAC          │
│    crowsnest_ui    LVGL 9 — board-independent                    │
│    crowsnest_link  framing + capability handshake                │
│    crowsnest_input encoder/button shim        ← per-board        │
│    ESP-BSP         panel · touch · backlight  ← per-board        │
└──────────────────────────────────────────────────────────────────┘
```

### 3.1 Key design decisions

**D1 — The firmware is a thin client.** It holds no radio logic, no channel tables, no notion of MHz versus kHz. It receives a state frame and draws it; it sends input events and forgets them. Every behavioural change then happens in C#, where it is testable and does not require reflashing. The cost is a round trip on every detent; over USB CDC that is a few milliseconds, and the host echoes a new state frame immediately on receipt without waiting for the sim, so the knob feels direct.

**D2 — Powered USB hub, wired. Wi-Fi as an option, Bluetooth never.** This is settled by power, not by data. Each panel draws 5 V at up to 1 A and has no battery, so **a cable runs to every panel regardless of transport**. Bluetooth and Wi-Fi do not remove that cable; they add a radio next to it. Once a cable is mandatory, using it for data is nearly free.

| | USB (powered hub) | Wi-Fi | Bluetooth |
|---|---|---|---|
| Removes the cable | No — cable is mandatory anyway | No | No |
| Per-detent latency | ~1–3 ms | 5–20 ms, jittery under load | 30–100 ms+ |
| Setup per panel | Plug in | SSID, password, discovery, firewall | Pair, re-pair after each flash |
| Identity across reboots | Protocol handshake | Protocol handshake | Protocol handshake |
| Panel-side cost | None | RGB panel tearing from PSRAM contention | Same radio, same band |
| Scales to 5 panels | Yes | Yes, but 5× the provisioning | Poorly |

Three specifics behind that table:

- **The ESP32-S3 has no Bluetooth Classic.** It is BLE-only — Elecrow's own specification lists "Bluetooth Low Energy and Bluetooth 5.0" with no BR/EDR. That rules out the Serial Port Profile, so a Bluetooth build means a custom GATT service. Worse, Windows negotiates conservative BLE connection intervals, which puts per-detent latency in the tens of milliseconds. On a knob that is felt directly as lag, and it is the one place in this system where latency is not negotiable.
- **Wi-Fi does not actually reduce the mess.** BLE and 2.4 GHz Wi-Fi share a band, so the interference concern applies to both. The real Wi-Fi problem here is the one already in the spec: on ESP32-S3, an RGB-parallel panel streaming its framebuffer from PSRAM contends with the radio for memory bandwidth, and tearing under Wi-Fi load is a known class of problem on these boards.
- **Wi-Fi still earns a place, but later.** In a permanent cockpit build, panel power is often already solved by a distribution rail, so a Wi-Fi panel needs no data run to the PC. That is a real advantage for a mounted frame and the reason `IDeviceTransport` stays an abstraction — but it is a phase 7 convenience, not the primary path.

**Hub sizing is a real constraint, not a footnote.** Five panels at the rated 1 A is 5 A / 25 W. A bus-powered USB 2.0 hub supplies 500 mA per port and USB 3.0 supplies 900 mA; neither is close. Many hubs sold as "powered" ship a 2 A adapter shared across all ports. Specify a hub with a **40 W or better supply and per-port current of at least 1 A**, and treat brown-out under simultaneous full backlight as a thing to test rather than assume.

**D3 — Build our own SimConnect layer, behind an interface.** See §7. The instinct is right, but the interface matters more than the implementation.

**D4 — Use LVGL on the device.** See §9. Writing our own rendering is not a good use of the budget.

**D5 — WiX v5 MSI, not MSIX.** MSI gives the Add/Remove Programs entry directly, and imposes no constraints on native DLL loading, serial port access, or startup registration.

---

## 4. .NET solution layout

```
Crowsnest.sln
├── src/
│   ├── Crowsnest.Core/            net10.0              — no project references
│   ├── Crowsnest.SimConnect/      net10.0-windows;x64  — no project references
│   ├── Crowsnest.Sim/             net10.0-windows      — → Core, SimConnect
│   ├── Crowsnest.Device/          net10.0-windows      — → Core
│   ├── Crowsnest.Host/            net10.0-windows      — → Core, Sim, Device
│   ├── Crowsnest.Tray/            net10.0-windows WPF  — → Host  (entry point)
│   └── Crowsnest.Installer/       WiX v5
├── firmware/
│   └── crowsnest-display/         ESP-IDF 6.1 + LVGL 9
├── tools/
│   ├── Crowsnest.DevConsole/      manual SimConnect harness
│   └── Crowsnest.DeviceSimulator/ speaks the device protocol, no hardware needed
└── tests/
    ├── Crowsnest.Core.Tests/
    ├── Crowsnest.Device.Tests/
    └── Crowsnest.Sim.Tests/
```

**Dependency rule.** `Crowsnest.Core` references nothing but the BCL. Adapters (`Sim`, `Device`) reference `Core` and never each other. `Host` composes. This is Ports and Adapters, and it is what makes `Crowsnest.SimConnect` reusable in another project entirely.

`Crowsnest.SimConnect` deliberately has no reference to `Core` — it is a general-purpose SimConnect library that happens to be used here, and could be published as its own NuGet package.

### 4.1 Inside Crowsnest.Core

The layering above is a *deployment* structure. Within `Core`, the organising principle is the opposite: **vertical slices by panel**, so that everything about COM lives in one place rather than being smeared across a domain folder, a config file and a page catalogue.

```
src/Crowsnest.Core/
├── Domain/                        — shared, panel-agnostic
│   ├── ParameterId.cs
│   ├── ComFrequency.cs
│   ├── CursorLevel.cs
│   ├── Grids/                     IValueGrid + the grids two or more panels share
│   ├── Formatting/                IValueFormatter + shared formatters
│   └── Tuning/                    TuningSession, TuningOptions, acceleration
│
├── Application/                   — shared orchestration
│   ├── Ports/                     ISimParameterGateway, IPanelDevice
│   ├── Panels/                    IPanelModule, PanelBuilder, PanelComposer
│   ├── PanelCoordinator.cs
│   ├── PageNavigator.cs
│   └── ParameterRegistry.cs
│
└── Panels/                        — one folder per panel family
    ├── PanelCatalog.cs            the explicit registration list
    ├── Com/
    │   ├── ComPanelModule.cs      COM 1 + COM 2
    │   ├── ComChannelGrid.cs      + ChannelSpacing; COM is its only user
    │   ├── ComFrequencyFormatter.cs
    │   ├── ComSpacingBehaviour.cs
    │   └── com.parameters.json    embedded resource
    ├── Nav/                       phase 5
    ├── Autopilot/                 phase 6
    └── Transponder/               phase 6
```

Tests mirror it exactly — `tests/Crowsnest.Core.Tests/Panels/Com/` — so a panel and its tests are one `cd` apart.

**Folders, not projects.** Each panel is a module in the design sense (§5.8) without being a separate assembly. See §5.8 for the promotion criteria; the short version is that a panel earns its own `.csproj` when it needs its own NuGet dependencies or must load at runtime, and not before.

**Dependency direction inside Core.** `Panels/*` may reference `Domain/` and `Application/`. `Domain/` and `Application/` must never reference `Panels/` — enforced by an architecture test (§11), because this is exactly the rule that erodes quietly under deadline pressure. Panel folders must not reference each other either; anything two panels need moves down into `Domain/`.

## 5. Crowsnest.Core

### 5.0 The generalisation: everything is a tunable parameter

COM 1 is the first of perhaps fifteen things this panel will eventually control. NAV 1/2, COM 2, ADF, transponder, and the autopilot's altitude, heading, vertical speed and airspeed all share one shape:

> a value that lives in the sim, a set of legal values it may take, a cursor selecting which digit group the knob moves, a way to read it, and a way to write it.

They differ only in the details. So the domain models **parameters**, not radios.

**The canonical unit rule.** Every parameter's value is a plain `int` in a canonical unit — kHz for frequencies, feet for altitude, degrees for heading, feet-per-minute for vertical speed, knots for airspeed, a packed octal integer for a squawk code. This is what makes the generalisation cheap: the `TuningSession` state machine, which is the hardest-won part of the design, needs **no changes at all**. It already operated on a single value with a pending/confirmed lifecycle. Only the value's *type* was too specific, and the fix is to stop having one.

```csharp
public readonly record struct ParameterId(string Key);
// "com1.active", "com1.standby", "nav1.standby", "ap.altitude", "xpdr.code"
```

String keys rather than an enum, deliberately: parameters are defined in data (§5.2), so adding NAV 2 must not require touching a `switch` statement or recompiling to add an enum member.

### 5.1 Value objects

`namespace Crowsnest.Core.Domain`

```csharp
public enum CanonicalUnit { Kilohertz, Feet, Degrees, FeetPerMinute, Knots, Millibars, OctalCode }
public enum ValueSlot      { Active, Standby, Single }

// Retained as a formatting/parsing helper used by the frequency grids and tests.
// NOT the transport type — TuningSession works on int.
public readonly record struct ComFrequency : IComparable<ComFrequency>
{
    public int Kilohertz { get; }
    public int Megahertz   => Kilohertz / 1000;
    public int FractionKhz => Kilohertz % 1000;

    public static ComFrequency FromKhz(int khz);
    public static ComFrequency FromMhz(double mhz);   // rounds to nearest kHz
    public static bool TryParse(ReadOnlySpan<char> text, out ComFrequency value);
    public long   ToHz();
    public ushort ToBcd16();
    public string ToDisplayString();
}
```

**Never represent a frequency as a `double` MHz value inside the domain.** Integer kHz eliminates an entire class of 8.33 kHz rounding bug. `double` appears only at the SimConnect boundary, in one place.

### 5.2 Parameter definitions and the registry

This is the change that makes "extend rapidly" true. A parameter is described by data, not code.

```csharp
public sealed record ParameterDefinition(
    ParameterId Id,
    string Label,                          // "COM 1 STBY"
    string GroupId,                        // "com1" — binds an active/standby pair together
    CanonicalUnit Unit,
    IValueGrid Grid,
    IReadOnlyList<CursorLevel> Cursors,    // ordered coarse → fine
    ReadBinding Read,
    WriteBinding Write,
    IValueFormatter Formatter);

public sealed record CursorLevel(string Name, int Step, CursorWrap Wrap, Range DisplaySpan);
public enum CursorWrap { WrapWithinParent, Carry, Clamp }

public sealed record ReadBinding(ReadSource Source, string Name, string Unit, double Scale);
public enum ReadSource { SimVar, LVar, Calculator }

public sealed record WriteBinding(WriteMode Mode, string Target, PayloadEncoding Encoding);
public enum WriteMode        { KeyEvent, SimVarWrite, LVarWrite }
public enum PayloadEncoding  { Hz, Bcd16, Raw, Signed }

public sealed record ParameterGroup(
    string Id, string Title,
    ParameterId? Active, ParameterId? Standby,
    string? SwapEvent);                    // null for parameters with no standby concept

public sealed class ParameterRegistry
{
    public static ParameterRegistry LoadDefault();          // embedded JSON resource
    public static ParameterRegistry FromJson(Stream json);  // user override, phase 6

    public ParameterDefinition this[ParameterId id] { get; }
    public IReadOnlyList<ParameterGroup> Groups { get; }
    public IReadOnlyList<ReadBinding> AllReadBindings { get; }   // drives one bulk subscription
}
```

`CursorLevel.DisplaySpan` is the character range the cursor underlines in the formatted string — `4..7` for the kHz digits of `"121.500"`. The host computes it; the firmware just underlines those characters. This is what lets a page for autopilot altitude reuse the same rendering code as a COM page.

**Registry entries.** Held as embedded JSON in the repo, strongly typed at load, validated by a test that round-trips every entry:

```jsonc
{
  "id": "com1.standby", "label": "COM 1 STBY", "group": "com1", "unit": "kHz",
  "grid":    { "type": "comChannel", "min": 118000, "max": 136990, "spacingFrom": "com1.spacing" },
  "cursors": [ { "name": "mhz", "step": 1000, "wrap": "clamp" },
               { "name": "khz", "step": 1,    "wrap": "wrapWithinParent", "span": "4..7" } ],
  "read":    { "source": "simvar", "name": "COM STANDBY FREQUENCY:1", "unit": "MHz", "scale": 1000 },
  "write":   { "mode": "keyEvent", "target": "COM_STBY_RADIO_SET_HZ", "encoding": "hz" },
  "format":  "freq3"
}
```

```jsonc
{
  "id": "nav1.standby", "label": "NAV 1 STBY", "group": "nav1", "unit": "kHz",
  "grid":    { "type": "linear", "min": 108000, "max": 117950, "step": 50 },
  "cursors": [ { "name": "mhz", "step": 1000, "wrap": "clamp" },
               { "name": "khz", "step": 50,   "wrap": "wrapWithinParent", "span": "4..6" } ],
  "read":    { "source": "simvar", "name": "NAV STANDBY FREQUENCY:1", "unit": "MHz", "scale": 1000 },
  "write":   { "mode": "keyEvent", "target": "NAV1_STBY_SET_HZ", "encoding": "hz" },
  "format":  "freq2"
}
```

```jsonc
{
  "id": "ap.altitude", "label": "ALTITUDE", "group": "ap.alt", "unit": "ft",
  "grid":    { "type": "linear", "min": 0, "max": 50000, "step": 100 },
  "cursors": [ { "name": "coarse", "step": 1000 }, { "name": "fine", "step": 100 } ],
  "read":    { "source": "simvar", "name": "AUTOPILOT ALTITUDE LOCK VAR", "unit": "feet" },
  "write":   { "mode": "keyEvent", "target": "AP_ALT_VAR_SET_ENGLISH", "encoding": "raw" },
  "format":  "thousands"
}
```

```jsonc
{
  "id": "ap.heading", "label": "HEADING", "group": "ap.hdg", "unit": "deg",
  "grid":    { "type": "wrapping", "min": 0, "max": 359, "step": 1 },
  "cursors": [ { "name": "tens", "step": 10 }, { "name": "ones", "step": 1 } ],
  "read":    { "source": "simvar", "name": "AUTOPILOT HEADING LOCK DIR", "unit": "degrees" },
  "write":   { "mode": "keyEvent", "target": "HEADING_BUG_SET", "encoding": "raw" },
  "format":  "deg3"
}
```

Adding NAV 2 is a JSON entry. Adding the whole autopilot is four. Feature work becomes content work, and the C# that has to be reviewed and tested stops growing.

**The over-engineering guard.** Do not ship this as a user-editable file in v1. It is an internal representation that happens to be expressed as JSON, kept in the repo, covered by tests. Exposing it for user editing is a phase-6 decision that can be made once the shape has stopped moving.

### 5.3 Value grids

```csharp
public interface IValueGrid
{
    bool Contains(int value);
    int  Snap(int value);                                   // nearest legal value
    int  Step(int from, int detents, CursorLevel cursor);
}

public sealed class ComChannelGrid   : IValueGrid;   // Panels/Com — 8.33 / 25 kHz ICAO channel naming
public sealed class LinearGrid       : IValueGrid;   // NAV 50 kHz, AP altitude 100 ft
public sealed class WrappingGrid     : IValueGrid;   // heading, course: 359 → 0
public sealed class SignedLinearGrid : IValueGrid;   // vertical speed, ±100 fpm
public sealed class DigitGrid        : IValueGrid;   // Panels/Transponder — four independent octal digits
```

`ComChannelGrid` is the v0.1 `ComChannelTable`, unchanged in behaviour: a precomputed ordered table of legal channels with index arithmetic for stepping. It is now one implementation among several rather than the centre of the design.

The 8.33 kHz rule it encodes is unchanged and remains the subtlest thing in the codebase — within each 100 kHz block the legal fractions are `.000 .005 .010 .015 .025 .030 .035 .040 .050 .055 .060 .065 .075 .080 .085 .090`, with `.020 .045 .070 .095` omitted. NAV is far simpler: plain 50 kHz linear steps, which is why it gets `LinearGrid`.

**Implemented 2026-09-24** in `Panels/Com/ComChannelGrid.cs`, with `ChannelSpacing` beside it. It lives in the COM module rather than `Domain/Grids/` by the §4.1 rule: only one panel uses it, and the module hands it to the registry through `PanelBuilder.AddGrid` (§5.8). `DigitGrid` goes to `Panels/Transponder/` for the same reason. The rule reduces to arithmetic: a kHz value is an 8.33 channel name when `khz % 25` is 0, 5, 10 or 15, and a 25 kHz channel when it is 0. The table exists for stepping, not for membership. A cursor whose `Step` is a whole-MHz multiple moves the MHz and keeps the fraction; any other `Step` counts channels. Exact snap ties go down.

**Why the grid is ours rather than the sim's.** SimConnect offers relative events (`COM_RADIO_FRACT_INC` and friends) that would let the sim apply its own spacing. Rejected: every detent would wait on a sim frame before the panel could show it, which is fine on the reference machine and not on a laptop at 25 fps; relative writes are not idempotent, so a dropped or retried event drifts where an absolute write self-corrects; and the pending/confirmed model in §5.4 assumes absolute values. The panel steps locally from this grid and the sim confirms.

**Choosing the spacing.** Follow the `COM SPACING MODE:n` SimVar (`Enum`: 0 = 25 kHz, 1 = 8.33), subscribed like any other value so a mid-flight toggle is followed, and rebuild the grid on every change. **The SimVar is the only evidence.** The sim does not police spacing (below), so a frequency it reports may be one we wrote, and says nothing about what the aircraft supports. **The host must therefore never write a channel that is illegal in the current mode** — the grid is the only guard there is. On a change to 25 kHz, expect the sim to snap standby itself; `ObserveSimValue` picks that up like any other external change. Log the mode in tray diagnostics.

**Verified 2026-09-24** with `Crowsnest.SimSpike` in the Carenado C185 at a UK airport:

- **The sim works in channel names, not true frequencies.** `COM_STBY_RADIO_SET_HZ 118005000` reads back as exactly `118005000` Hz. The gateway's conversion is `kHz × 1000` with no 8.33 mapping.
- **`COM SPACING MODE:1` is honoured by a third-party aircraft**, and follows `COM_1_SPACING_MODE_SWITCH` in both directions. The C185 defaults to **25 kHz even at a UK airport** — spacing is the aircraft's choice, not the region's.
- **The sim accepts off-spacing writes.** `118.005` written while in 25 kHz mode was stored and read back unchanged — no rejection, no snap. A wrong 8.33 guess is therefore *silent*, not surfaced by the settle timeout.
- **Switching 8.33 → 25 kHz snaps standby in the sim**: `119.005` became `119.000`, matching `ComChannelGrid.Snap`.
- **Values read during flight load are transient.** The spike was started before the flight finished loading. Its first samples showed a placeholder (active = standby = `124.850`) and spacing 8.33; about 24 s later the aircraft's own initialisation set active to `127.850` (passing through `134.380`) and spacing to 25 kHz, with no user input. The self-test's writes landed during that window and read back at 25–42 ms — **not comparable** to the 10–16 ms C172 figure; manual writes after load read back in 4.5–11 ms. **Rule for `Crowsnest.Sim` (§7.3): wait for the `SimStart` system event before writing anything or treating a value as confirmed,** because an aircraft's initialisation can overwrite a write made during load. Changes after that point need no special handling — they are external changes to `ObserveSimValue`, and a spacing change rebuilds the grid.

Still open: study-level aircraft, and whether any aircraft reports 8.33 capability it does not actually have.

**Roadmap coverage.** Everything planned is reachable with these five grids:

| Feature | Grid | Cursors | Read | Write |
|---|---|---|---|---|
| COM 1/2 active + standby | `ComChannelGrid` | MHz, kHz | SimVar MHz | `COM*_SET_HZ` |
| NAV 1/2 active + standby | `LinearGrid` 50 kHz | MHz, kHz | SimVar MHz | `NAV*_SET_HZ` |
| OBS / course | `WrappingGrid` | tens, ones | SimVar degrees | `VOR*_SET` |
| Transponder | `DigitGrid` octal ×4 | per digit | SimVar BCO16 | `XPNDR_SET` |
| AP altitude | `LinearGrid` 100 ft | 1000, 100 | SimVar feet | `AP_ALT_VAR_SET_ENGLISH` |
| AP heading | `WrappingGrid` | tens, ones | SimVar degrees | `HEADING_BUG_SET` |
| AP vertical speed | `SignedLinearGrid` | 100 fpm | SimVar fpm | `AP_VS_VAR_SET_ENGLISH` |
| AP airspeed | `LinearGrid` 1 kt | tens, ones | SimVar knots | `AP_SPD_VAR_SET` |
| Barometer | `LinearGrid` 0.01 inHg | — | SimVar mb | `KOHLSMAN_SET` |

The one that does not fit is a study-level Airbus or PMDG autopilot, which exposes its state through custom L-vars rather than standard SimVars. That is why `ReadSource` and `WriteMode` already have `LVar` members — the registry can describe those bindings, and the gateway grows an L-var path (via a WASM module such as MobiFlight's) without the domain noticing. Do not build that in v1; just leave the door open, which costs two enum members.

### 5.4 The tuning state machine

Unchanged from v0.1 except that `ComFrequency` becomes `int`. Restated here for completeness because it remains the heart of the system.

```csharp
public enum PendingWriteStatus { None, AwaitingConfirmation, Confirmed, Rejected }

public sealed class TuningSession
{
    public TuningSession(ParameterDefinition parameter, TuningOptions options);

    public int                Displayed  { get; }   // pending value if any, else confirmed
    public int                Confirmed  { get; }   // last value the sim agreed to
    public CursorLevel        Cursor     { get; }
    public PendingWriteStatus Status     { get; }

    public TuningOutcome ApplyDetents (int detents, TimeSpan sinceLastDetent, DateTimeOffset now);
    public TuningOutcome ToggleCursor (DateTimeOffset now);      // cycles the Cursors list
    public TuningOutcome ObserveSimValue(int value, DateTimeOffset now);
    public TuningOutcome Tick         (DateTimeOffset now);      // debounce + settle timers
}

public readonly record struct TuningOutcome(bool DisplayChanged, int? WriteRequest, PendingWriteStatus Status);

public sealed record TuningOptions(
    TimeSpan WriteDebounce    /* 120 ms  */,
    TimeSpan MaxWriteInterval /* 300 ms  */,
    TimeSpan SettleTimeout    /* 1500 ms */,
    IEncoderAcceleration Acceleration);

public interface IEncoderAcceleration { int Scale(int detents, TimeSpan sinceLastDetent); }
```

The class is pure — no clock, no logger, no I/O; `now` is a parameter. `ToggleCursor` now cycles through `ParameterDefinition.Cursors` rather than flipping a two-valued enum, which is how a three-level autopilot cursor comes for free.

**The reconciliation rule, unchanged.** While a pending value exists, inbound sim values are ignored unless they equal it (→ `Confirmed`). If a pending value stays outstanding beyond `SettleTimeout`, the session reverts to the sim's value and reports `Rejected`. Writes are coalesced by `WriteDebounce` but forced out every `MaxWriteInterval` so a long spin keeps streaming.

This matters *more* for autopilot than for radios. The sim writes to AP values continuously — VNAV steps the altitude, LNAV moves the heading bug — so the "sim disagrees with me" case stops being an edge case and becomes normal operation.

### 5.5 Pages

With fifteen parameters and one screen, the device needs a notion of where it is.

```csharp
public sealed record PanelPage(
    string Id, string Title,
    PageLayout Layout,
    IReadOnlyList<ParameterId> Fields,
    string? SwapEvent);

public enum PageLayout { ActiveStandbyPair, SingleValue, DualValue }

public sealed class PageNavigator
{
    public PanelPage Current { get; }
    public void Next();
    public void Previous();
    public bool TryGoTo(string pageId);
}
```

`PageLayout` is a small closed set the firmware implements literally; the host chooses which one and fills it. A COM or NAV page is `ActiveStandbyPair`, autopilot altitude is `SingleValue`. This is what preserves the thin-client property while letting parameters with different shapes share one firmware.

### 5.6 Ports

`namespace Crowsnest.Core.Application.Ports`

```csharp
public interface ISimParameterGateway : IAsyncDisposable
{
    IObservable<SimConnectionState> ConnectionState { get; }
    IAsyncEnumerable<ParameterSnapshot> Snapshots { get; }

    Task SubscribeAsync(IReadOnlyList<ParameterDefinition> parameters, CancellationToken ct);
    Task WriteAsync(ParameterId id, int canonicalValue, CancellationToken ct);
    Task InvokeAsync(string eventName, uint payload, CancellationToken ct);   // swaps, toggles
}

public sealed record ParameterSnapshot(ParameterId Id, int CanonicalValue, bool Available);

public interface IPanelDevice : IAsyncDisposable
{
    IObservable<DeviceConnectionState> ConnectionState { get; }
    DeviceCapabilities? Capabilities { get; }          // populated by the hello handshake
    IAsyncEnumerable<DeviceInputEvent> Inputs { get; }

    Task RenderAsync(DisplayFrame frame, CancellationToken ct);
}

public sealed record DeviceCapabilities(
    string DeviceType, string FirmwareVersion,
    ScreenShape Shape, int Width, int Height,
    bool HasEncoder, int DetentsPerClick,
    bool HasTouch, int ButtonCount, int MaxFields);

public abstract record DeviceInputEvent(long Sequence, DateTimeOffset At)
{
    public sealed record EncoderTurned(long Sequence, DateTimeOffset At, int Detents)    : DeviceInputEvent(Sequence, At);
    public sealed record KnobPressed  (long Sequence, DateTimeOffset At, PressKind Kind) : DeviceInputEvent(Sequence, At);
    public sealed record ScreenTapped (long Sequence, DateTimeOffset At, int X, int Y)   : DeviceInputEvent(Sequence, At);
    public sealed record SwipeDetected(long Sequence, DateTimeOffset At, SwipeDir Dir)   : DeviceInputEvent(Sequence, At);
}
public enum PressKind { Short, Long }
public enum SwipeDir  { Left, Right, Up, Down }
```

The device renders a `DisplayFrame` of **pre-formatted strings**, not values:

```csharp
public sealed record DisplayFrame(
    long Revision, SimConnectionState Sim,
    PageDescriptor Page, IReadOnlyList<FieldDescriptor> Fields,
    string? Notice, long AckSequence);

public sealed record PageDescriptor(string Id, string Title, PageLayout Layout, int Index, int Count);

public sealed record FieldDescriptor(
    FieldRole Role, string Label, string Text,
    Range? CursorSpan, bool Pending);

public enum FieldRole { Primary, Secondary, Tertiary }
```

This is the pivotal decision for extensibility. The firmware receives `"12,000"` with a cursor span, not an altitude in feet. It has no concept of frequencies, headings, or units. **Adding the entire autopilot requires zero firmware changes.**

### 5.7 Orchestration

```csharp
public sealed class PanelCoordinator : IAsyncDisposable
{
    public PanelCoordinator(
        ISimParameterGateway sim, IPanelDevice device,
        ParameterRegistry registry, IPageCatalog pages, IInputActionMap actions,
        IOptionsMonitor<BridgeOptions> options, TimeProvider time,
        ILogger<PanelCoordinator> log);

    public Task RunAsync(CancellationToken ct);
}
```

Internally it holds one `TuningSession` per `ParameterId`, a `PageNavigator`, and the same single-threaded event loop over a `Channel<BridgeEvent>` as v0.1. Device inputs, sim snapshots, connection transitions and a 20 ms tick all arrive on one queue and are processed sequentially. **There are no locks anywhere in `Core`.**

Only the parameters on the current page are ticked for debounce; the rest hold their confirmed values. Sim subscriptions cover every registered parameter regardless of page, so switching pages is instant rather than triggering a fresh subscription round trip.

```csharp
public interface IInputActionMap { BridgeCommand? Resolve(DeviceInputEvent e, PanelState state); }

public abstract record BridgeCommand
{
    public sealed record AdjustValue  (int Detents) : BridgeCommand;
    public sealed record CycleCursor                : BridgeCommand;
    public sealed record SwapSlots                  : BridgeCommand;   // no-op on pages without a pair
    public sealed record NextPage                   : BridgeCommand;
    public sealed record PreviousPage               : BridgeCommand;
    public sealed record GoToPage(string PageId)    : BridgeCommand;
}
```

Gesture bindings become configuration. The v1 map: turn → `AdjustValue`, short press → `CycleCursor`, tap → `SwapSlots`, long press → `NextPage`, swipe → `NextPage`/`PreviousPage` where touch supports it.

### 5.8 Panel modules

The registry in §5.2 makes a simple panel almost free to add, but it scatters that panel across the codebase: a JSON entry here, a page entry there, a formatter somewhere else, tests in a fourth place. Six months in, "how does COM work?" becomes a grep.

More importantly, the assumption that panels are *pure data* will not hold. Some already need behaviour:

- **COM** — the channel grid depends on `COM SPACING MODE:1`, so the parameter's legal values change at runtime based on another SimVar.
- **Transponder** — digit-by-digit octal entry with VFR and IDENT actions, which is not "one knob, one value."
- **Autopilot** — armed and captured modes are annunciation state, not a tunable value, and want a page layout of their own.

So panels are **modules**: a vertical slice owning everything about one panel family, contributing itself to the system through one interface.

```csharp
namespace Crowsnest.Core.Application.Panels;

public interface IPanelModule
{
    string Id    { get; }               // "com", "nav", "autopilot"
    int    Order { get; }               // page ordering; sparse, e.g. 10, 20, 30
    PanelRequirements Requires { get; }
    void Configure(PanelBuilder builder);
}

public sealed record PanelRequirements(
    bool NeedsEncoder = true,
    bool NeedsTouch   = false,
    int  MinButtons   = 1,
    int  MinFields    = 2,
    IReadOnlyList<PageLayout>? RequiredLayouts = null);

public sealed class PanelBuilder
{
    public PanelBuilder AddParametersFromJson(string embeddedResourceName);
    public PanelBuilder AddParameter(ParameterDefinition definition);
    public PanelBuilder AddPage(PanelPage page);
    public PanelBuilder AddFormatter(string key, IValueFormatter formatter);
    public PanelBuilder AddGrid(string key, IValueGrid grid);
    public PanelBuilder AddBehaviour<T>() where T : class, IPanelBehaviour;
    public PanelBuilder AddInputOverride(IInputActionMap map);
}

// Optional. Only panels with runtime logic implement this.
public interface IPanelBehaviour
{
    void OnSnapshot(ParameterSnapshot snapshot, IPanelContext context);
    void OnCommand(BridgeCommand command, IPanelContext context);
}
```

A simple panel is a dozen lines plus JSON:

```csharp
public sealed class ComPanelModule : IPanelModule
{
    public string Id    => "com";
    public int    Order => 10;
    public PanelRequirements Requires => new(MinFields: 2);

    public void Configure(PanelBuilder b) => b
        .AddParametersFromJson("Panels.Com.com.parameters.json")
        .AddFormatter("freq3", new FrequencyFormatter(decimals: 3))
        .AddBehaviour<ComSpacingBehaviour>()          // swaps the grid when spacing mode changes
        .AddPage(new PanelPage("com1", "COM 1", PageLayout.ActiveStandbyPair,
                               ["com1.standby", "com1.active"], SwapEvent: "COM_STBY_RADIO_SWAP"))
        .AddPage(new PanelPage("com2", "COM 2", PageLayout.ActiveStandbyPair,
                               ["com2.standby", "com2.active"], SwapEvent: "COM2_RADIO_SWAP"));
}
```

**Group by family, not by instance.** `Panels/Com/` holds both COM 1 and COM 2, because they are the same panel with a different index and share a formatter, a grid and a behaviour. `Panels/Com1/` and `Panels/Com2/` would duplicate all three. The same applies to NAV.

**Discovery is an explicit list, never assembly scanning.**

```csharp
public static class PanelCatalog
{
    public static IReadOnlyList<IPanelModule> All =>
    [
        new ComPanelModule(),          // 10
        new NavPanelModule(),          // 20   phase 5
        new AutopilotPanelModule(),    // 30   phase 6
        new TransponderPanelModule(),  // 40   phase 6
    ];
}
```

Reflection-based discovery is slower to start, breaks trimming and AOT, hides ordering, and makes "why is this page here?" un-greppable. One line per panel in one file costs nothing and answers that question immediately.

**Requirements gate the page catalogue.** At handshake time the host filters modules against the `DeviceCapabilities` reported by the device (§5.6). A module needing two buttons is silently dropped on a board with one, rather than producing a page the user cannot operate. This is the piece that makes the module boundary pay for itself twice: it is both the feature seam and the portability seam.

```csharp
public sealed class PanelComposer
{
    public PanelComposition Compose(IReadOnlyList<IPanelModule> modules, DeviceCapabilities caps);
}
```

**When to promote a folder to a project.** Not yet, and possibly never. Promote `Panels/Autopilot/` to `Crowsnest.Panels.Autopilot.csproj` only when one of these is true: it needs NuGet dependencies the rest of `Core` should not carry; it must ship or version independently; or third-party panels need loading at runtime. Assembly boundaries should be driven by deployment and reuse, not by conceptual tidiness — fifteen projects would mean fifteen `.csproj` files, fifteen DI registrations and a slower build in exchange for nothing.

## 6. Crowsnest.Device

```csharp
namespace Crowsnest.Device;

public interface IDeviceTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct);
    PipeReader Input  { get; }
    PipeWriter Output { get; }
    IObservable<bool> IsConnected { get; }
}

public sealed class SerialPortTransport : IDeviceTransport;   // v1
public sealed class WebSocketTransport  : IDeviceTransport;   // phase 7
public sealed class LoopbackTransport   : IDeviceTransport;   // tests

public sealed class NdjsonFrameReader;            // System.IO.Pipelines, splits on '\n'
public sealed class NdjsonFrameWriter;
public sealed class ProtocolCodec;
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(DeviceMessage))]
public partial class ProtocolJsonContext : JsonSerializerContext;   // source-generated

public sealed class PanelDeviceConnection : IPanelDevice;   // transport + codec + heartbeat
public sealed class CapabilityNegotiator;                   // hello → DeviceCapabilities
public sealed class DeviceDiscovery : IDeviceDiscovery;
public sealed class HeartbeatMonitor;
public sealed class DeviceReconnectPolicy;                  // exponential backoff + jitter
```

**Discovery.** `SerialPort.GetPortNames()` gives no VID/PID, so use a CIM/WMI query over `Win32_PnPEntity` to enumerate ports with hardware IDs. Filter to the board's USB descriptor, then probe by writing a `hello` frame and waiting 500 ms for a reply. Cache the last-good port in settings and try it first. Because the protocol identifies the device in its `hello` response, probing works for any supported board without a per-board VID/PID table.

> **Resolved during the hardware spike — see F4 in §9.6.** The board does *not* use a CH34x bridge. It exposes the ESP32-S3's native USB-Serial/JTAG peripheral and enumerates as `VID_303A&PID_1001`. Filter on that. Baud rate is meaningless over a virtual CDC port, so `SerialPortTransport` may set any value. Retain the probe-every-candidate-port fallback regardless: it is what makes discovery work for board ports that *do* use a bridge.

### 6.1 Wire protocol

Newline-delimited JSON, UTF-8, one object per line. Chosen over binary framing because the message rate is low and being able to read the link with a terminal is worth more than the bytes saved. Protocol version is negotiated in the handshake.

**The central rule: the host sends text, not values.** Fields cross the wire as formatted strings with a cursor character span. The firmware renders characters and underlines a range. It has no concept of frequencies, altitudes, headings, or units — which is what allows NAV, COM 2 and the entire autopilot to be added without touching firmware.

**Device → Host**

```json
{"v":2,"t":"hello","dev":"crowpanel-2.1-rotary","fw":"1.0.0","id":"a4cf12de9010",
 "caps":{"shape":"round","w":480,"h":480,"encoder":true,"detentsPerClick":4,
         "touch":true,"buttons":1,"maxFields":3,"layouts":["pair","single","dual"]}}
{"v":2,"t":"input","seq":42,"ev":"encoder","d":-3}
{"v":2,"t":"input","seq":43,"ev":"press","kind":"short"}
{"v":2,"t":"input","seq":44,"ev":"tap","x":240,"y":180}
{"v":2,"t":"input","seq":45,"ev":"swipe","dir":"left"}
{"v":2,"t":"pong","ts":918273}
{"v":2,"t":"log","lvl":"warn","msg":"i2c timeout on pcf8574"}
```

**Host → Device**

```json
{"v":2,"t":"hello","host":"Crowsnest 1.0.0"}
{"v":2,"t":"hello_ack","proto":2,"cfg":{"brightness":80,"theme":"night"}}

// COM 1 — active/standby pair
{"v":2,"t":"state","rev":1057,"ack":44,"sim":"connected",
 "page":{"id":"com1","title":"COM 1","layout":"pair","index":0,"count":6},
 "fields":[{"role":"primary","label":"STBY","text":"121.500","cursor":[4,7],"pending":true},
           {"role":"secondary","label":"ACTIVE","text":"122.800"}]}

// Autopilot altitude — same firmware, same renderer, zero new code
{"v":2,"t":"state","rev":1058,"ack":46,"sim":"connected",
 "page":{"id":"ap.alt","title":"ALTITUDE","layout":"single","index":4,"count":6},
 "fields":[{"role":"primary","label":"FT","text":"12,000","cursor":[0,2]}]}

{"v":2,"t":"notice","kind":"aircraft_rejected"}
{"v":2,"t":"ping","ts":918273}
```

Protocol rules:

- `caps` in the device `hello` drives host-side layout selection. A board with no touch never receives a page whose only swap gesture is a tap; a board with a smaller screen gets shorter labels. The host adapts to the device, not the reverse.
- `rev` is monotonic; the device discards any frame with a lower `rev` than the last applied.
- `ack` echoes the highest input `seq` the host has processed, letting the device detect dropped input.
- `cursor` is a `[start, end)` character range into `text`.
- `ping`/`pong` every 2 s; three missed pongs on either side triggers a reconnect.
- `ts` is an **opaque 64-bit correlation token, not a time**. The host chooses the value, the device echoes it back unchanged in the `pong`, and the device never interprets, rescales or narrows it. The host matches a pong to its outstanding ping by that value alone, so a device that echoes anything else matches nothing. This is the contract a 32-bit `ts` in the panel firmware broke during first bring-up: the host keys its in-flight pings on `Stopwatch.GetTimestamp()`, which passes `INT32_MAX` a few minutes after boot, so every pong came back saturated at `2147483647` and the handshake completed but the first measurement never returned. Both ends carry this as a 64-bit integer. Every host-side wait on a pong is also bounded, so a device that gets this wrong surfaces as a named timeout rather than a silent hang on a link that still looks alive.
- Unknown message types and unknown fields are ignored rather than treated as errors, so the two sides version independently.

### 6.2 Multiple panels

Five panels on a hub is a bigger architectural change than the transport choice. It is also the change that makes the panel module system from §5.8 pay off a third time: **a physical panel is an assignment of pages to a device**.

```csharp
public interface IPanelDeviceManager : IAsyncDisposable
{
    IObservable<RosterChanged> Roster { get; }
    IReadOnlyList<ConnectedPanel> Connected { get; }
    Task StartAsync(CancellationToken ct);       // continuous scan + reconnect
}

public sealed record ConnectedPanel(
    DeviceIdentity Identity,
    DeviceCapabilities Capabilities,
    IPanelDevice Device,
    PanelAssignment? Assignment);

public sealed record DeviceIdentity(
    string HardwareId,        // eFuse MAC — stable across reflash, replug and hub port
    string DeviceType,        // "crowpanel-2.1-rotary"
    string FirmwareVersion);

public sealed record PanelAssignment(string Name, IReadOnlyList<string> PageIds);
```

**Identity is protocol-level, never port-level.** Five identical boards on a hub enumerate as five identical VID/PID devices, and Windows will happily hand out different COM port numbers after a reboot or a replug. `HardwareId` from the `hello` frame is the only stable handle. Nothing in the system may key off a port name.

Assignments persist in settings keyed by hardware ID:

```jsonc
"panels": {
  "a4cf12de9010": { "name": "Radios",    "pages": ["com1", "com2", "nav1", "nav2"] },
  "a4cf12de9f44": { "name": "Autopilot", "pages": ["ap.alt", "ap.hdg", "ap.vs"] },
  "a4cf12de7721": { "name": "Transponder", "pages": ["xpdr"] }
}
```

An unassigned panel renders an "unassigned" screen showing the last six characters of its hardware ID, and the tray raises a notification. The user matches the ID on screen to an entry in settings — which is how you tell five identical black discs apart without unplugging them one at a time.

**Coordinator shape: one loop, many devices.** Do not instantiate a `PanelCoordinator` per device. Two panels showing COM 1 must share one `TuningSession`, or they will fight each other's pending writes. Instead the existing coordinator grows a device dimension:

```csharp
internal sealed record DeviceInputReceived(string HardwareId, DeviceInputEvent Input) : BridgeEvent;
internal sealed record DeviceJoined(ConnectedPanel Panel)  : BridgeEvent;
internal sealed record DeviceLeft(string HardwareId)       : BridgeEvent;
```

All inputs from all panels land in the same `Channel<BridgeEvent>` and are processed on one thread. Sessions live in a shared store keyed by `ParameterId`, so state is naturally consistent across panels. Renders fan out: each device receives a `DisplayFrame` built only from the pages assigned to it. The no-locks property of §5.7 survives intact, and it is the main reason not to shard the coordinator.

Each device keeps its own `PageNavigator` — panels navigate independently even though they share tuning state.

**Sim subscription is unaffected.** One gateway, one data definition covering every registered parameter, regardless of how many panels are plugged in.

### 6.3 Firmware updates over the link

Detecting panels and offering to update them is the right call, and it is worth more here than with one device — nobody wants to flash five boards by hand. But do it **over the existing protocol**, not by shelling out to `esptool`.

```json
{"v":2,"t":"ota_begin","size":1843200,"sha256":"9f2c...","version":"1.1.0"}
{"v":2,"t":"ota_chunk","seq":0,"data":"<base64>"}
{"v":2,"t":"ota_commit"}
{"v":2,"t":"ota_status","state":"writing","pct":42}
```

The firmware writes into the inactive OTA partition, verifies the hash, marks it bootable and reboots; ESP-IDF's rollback support reverts automatically if the new image fails to check in. The 16 MB flash holds two OTA slots comfortably.

Why this rather than driving `esptool`:

- **No bootloader gymnastics.** Entering download mode via a USB-UART bridge depends on the DTR/RTS auto-reset circuit behaving, which is board-specific and a common source of "works on my desk" bugs. OTA sidesteps it entirely.
- **Transport-agnostic.** The same code path updates a panel over USB today and over Wi-Fi in phase 7. An esptool-based flow only ever works over serial.
- **No GPL entanglement.** esptool is GPL-2.0, which is a live question if Crowsnest ships as a signed closed-source installer. Worth resolving before it becomes a dependency rather than after.
- **Better failure mode.** A half-written OTA partition boots the old firmware. A half-written flash via esptool bricks the panel until someone finds the BOOT button.

Keep an esptool path documented for initial provisioning of a blank board, where there is no Crowsnest firmware to talk to. That is a one-time, per-board operation and a reasonable place to require a manual step.

Update flow in the tray: on connect, compare `DeviceCapabilities.FirmwareVersion` against the version bundled with the installed app; if it is older, offer a single "Update all panels" action that walks the roster sequentially. Never update in parallel — a brown-out during a simultaneous five-panel flash is exactly the failure the hub sizing note is warning about.

## 7. Crowsnest.SimConnect — the SimConnect library

### 7.1 Should we build our own?

The honest answer is: **build the interface ourselves, and start with a P/Invoke implementation, but do not treat the managed wrapper as unusable.**

The case against the shipped `Microsoft.FlightSimulator.SimConnect.dll` is well known — it targets .NET Framework, it must sit beside the executable along with the native `SimConnect.dll`, it is x64-only, and its API is a direct transliteration of the C API with `Enum`-typed IDs and a Windows-message-pump idiom that does not suit a headless background service.

However, at least one developer reports running it successfully under .NET 10, so the compatibility story appears better than its reputation. **Treat this as the first spike, not a settled fact.** Build a throwaway console app in week one that references the managed wrapper from a .NET 10 project and opens a connection. The result determines the implementation, not the design.

Either way the abstraction stands:

```csharp
public interface ISimConnectClient : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(string clientName, CancellationToken ct);

    DataDefinitionId DefineData<T>() where T : struct;
    void RequestDataOnSimObject(RequestId request, DataDefinitionId definition,
                                SimObjectId obj, SimConnectPeriod period, DataRequestFlags flags);
    ClientEventId MapClientEventToSimEvent(string eventName);
    void TransmitClientEvent(ClientEventId id, uint data);

    ChannelReader<SimConnectMessage> Messages { get; }
}
```

Three implementations sit behind it: `NativeSimConnectClient` (our P/Invoke, the target), `ManagedWrapperSimConnectClient` (a thin adapter, the insurance policy), and `FakeSimConnectClient` (so the rest of the system develops without the sim running). Swapping is one line of DI registration.

Building our own also buys a genuinely nicer programming model: `IAsyncEnumerable` and `Channel<T>` instead of event handlers, `SafeHandle` lifetime management, strongly-typed ID wrappers instead of raw `Enum`, attribute-driven data definitions, and source-generated `[LibraryImport]` marshalling.

### 7.2 Classes

```csharp
namespace Crowsnest.SimConnect;

internal static partial class NativeMethods
{
    [LibraryImport("SimConnect.dll", EntryPoint = "SimConnect_Open",
                   StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(out nint handle, string name, nint hWnd,
                                     uint userEventWin32, nint eventHandle, uint configIndex);
    // AddToDataDefinition, RequestDataOnSimObject, MapClientEventToSimEvent,
    // TransmitClientEvent_EX1, GetNextDispatch, SubscribeToSystemEvent, Close
}

public sealed class SimConnectHandle : SafeHandleZeroOrMinusOneIsInvalid;

public sealed class SimConnectClient     : ISimConnectClient;
public sealed class SimConnectSupervisor : ISimConnectClient;   // decorator: reconnect w/ backoff
public sealed class SimConnectDispatchLoop : IDisposable;       // dedicated thread → Channel<T>

public sealed class DataDefinitionBuilder;                      // builder
public sealed class StructMarshaller<T> where T : struct;       // cached offsets, MemoryMarshal
[AttributeUsage(AttributeTargets.Field)]
public sealed class SimVarAttribute : Attribute;

public sealed class SimConnectException : Exception;            // maps SIMCONNECT_EXCEPTION codes

public enum SimConnectPeriod { Never, Once, VisualFrame, SimFrame, Second }
public abstract record SimConnectMessage
{
    public sealed record Opened(string ApplicationName, Version SimVersion) : SimConnectMessage;
    public sealed record Quit                                              : SimConnectMessage;
    public sealed record Failed(uint RequestId, SimConnectExceptionCode Code) : SimConnectMessage;
    public sealed record ObjectData(uint RequestId, ReadOnlyMemory<byte> Payload) : SimConnectMessage;
}
```

Implementation notes that will otherwise bite:

- **Use a dedicated thread for the dispatch loop**, not the thread pool. `SimConnect_GetNextDispatch` is a poll; parking a pool thread on it starves the pool. If `SimConnect_CallDispatch` is used instead, the callback delegate must be pinned with a `GCHandle` for the lifetime of the connection or the GC will collect it and the process will crash unpredictably.
- **Deployment.** The native `SimConnect.dll` must be x64 and resolvable at load time. Either place it beside the executable or register a `NativeLibrary.SetDllImportResolver` that loads it from an `x64/` subfolder. Confirm redistribution terms in the MSFS SDK licence before bundling it in the installer.
- **Reconnection.** The sim may not be running at startup, may be restarted, and sends a `Quit` message on shutdown. `SimConnectSupervisor` wraps the client and retries on a jittered exponential backoff from 1 s to 30 s. This is a decorator, so `SimConnectClient` itself stays simple.

### 7.3 Crowsnest.Sim — the gateway

```csharp
namespace Crowsnest.Sim;

public sealed class SimConnectParameterGateway : ISimParameterGateway;
public sealed class BindingCompiler;        // ReadBinding[] → SimConnect data definitions
public sealed class WriteDispatcher;        // WriteBinding → key event or SetDataOnSimObject
public sealed class PayloadEncoder;         // Hz / BCD16 / raw / signed
public sealed class SimVersionProbe;        // MSFS 2020 vs 2024
public sealed class FakeParameterGateway : ISimParameterGateway;
```

The gateway is driven entirely by the registry. `BindingCompiler` walks `ParameterRegistry.AllReadBindings`, groups them into a single SimConnect data definition, and subscribes once on `SIMCONNECT_PERIOD_SECOND` with `CHANGED` — radio and autopilot values change rarely, and per-frame subscriptions are wasteful. Adding a parameter adds a field to the definition; no gateway code changes.

**Reading.**

| SimVar | Unit | Note |
|---|---|---|
| `COM ACTIVE FREQUENCY:1` | MHz (FLOAT64) | See warning below |
| `COM STANDBY FREQUENCY:1` | MHz (FLOAT64) | |
| `COM SPACING MODE:1` | enum | 25 kHz vs 8.33 kHz |
| `NAV ACTIVE FREQUENCY:1` | MHz (FLOAT64) | phase 5 |
| `NAV STANDBY FREQUENCY:1` | MHz (FLOAT64) | phase 5 |
| `AUTOPILOT ALTITUDE LOCK VAR` | feet | phase 6 |
| `AUTOPILOT HEADING LOCK DIR` | degrees | phase 6 |
| `AUTOPILOT VERTICAL HOLD VAR` | feet/minute | phase 6, signed |
| `AUTOPILOT AIRSPEED HOLD VAR` | knots | phase 6 |

> **Do not read frequencies as `Frequency BCD16`.** BCD16 carries only two decimal places, so `120.855` truncates to `120.85` and every 8.33 kHz channel with a non-zero third decimal reads back wrong. Read as MHz in FLOAT64 and round to the nearest kHz — `(int)Math.Round(mhz * 1000.0)` is exact at this magnitude. If the target SDK accepts `Hz` as a unit for these variables, prefer it and skip the conversion. Verify during the spike.

**Writing.** Two modes, selected per parameter by `WriteBinding.Mode`:

| Action | Event | Payload |
|---|---|---|
| COM 1 standby | `COM_STBY_RADIO_SET_HZ` | Hz, e.g. `122800000` |
| COM 1 active | `COM_RADIO_SET_HZ` | Hz — note there is **no** `COM1_RADIO_SET_HZ` |
| COM 1 swap | `COM_STBY_RADIO_SWAP` | 0 |
| COM 2 standby | `COM2_STBY_RADIO_SET_HZ` | Hz |
| NAV 1 standby | `NAV1_STBY_SET_HZ` | Hz |
| AP altitude | `AP_ALT_VAR_SET_ENGLISH` | feet |
| AP heading | `HEADING_BUG_SET` | degrees |
| AP vertical speed | `AP_VS_VAR_SET_ENGLISH` | fpm, signed |
| Spacing toggle | `COM_1_SPACING_MODE_SWITCH` | 0 |

Two gotchas worth writing down. COM 1 uses the unnumbered `COM_RADIO_SET_HZ`; only COM 2 and COM 3 carry an index. And the `_HZ` variants were undocumented for years but now appear in the MSFS 2024 key event reference — they are the only way to set a third decimal place.

**Avoid the `_INC` / `_DEC` event families entirely.** Increment events give away control of the value, behave inconsistently across aircraft, and make the pending/confirmed reconciliation in §5.4 impossible. Always compute the target locally and set it absolutely. This applies to autopilot values as much as frequencies.

Some autopilot parameters are also directly writable via `SimConnect_SetDataOnSimObject`, which is why `WriteMode.SimVarWrite` exists. Prefer key events where one is available — they trigger the aircraft's own systems logic, whereas a direct SimVar write can bypass it and desync the aircraft's displays.

## 8. Crowsnest.Host and Crowsnest.Tray

```csharp
namespace Crowsnest.Host;

public static class HostBuilderExtensions { public static IHostApplicationBuilder AddCrowsnest(this IHostApplicationBuilder b); }

public sealed class SimConnectHostedService : BackgroundService;   // owns gateway lifetime
public sealed class DeviceHostedService     : BackgroundService;   // owns device lifetime
public sealed class BridgeHostedService     : BackgroundService;   // runs the coordinator

public sealed class BridgeOptions;            // IOptionsMonitor, hot-reloaded
public sealed class SettingsStore;            // atomic write to %LOCALAPPDATA%\Crowsnest\settings.json
public sealed class HealthSnapshotProvider;   // feeds the tray UI and the diagnostics window
```

```csharp
namespace Crowsnest.Tray;

public partial class App : System.Windows.Application;
public sealed class TrayIconController : IDisposable;   // H.NotifyIcon
public sealed class TrayViewModel;
public partial class SettingsWindow : Window;
public sealed class SettingsViewModel;
public sealed class DiagnosticsViewModel;               // live log tail + link stats
public sealed class StartupRegistration;                // HKCU\...\Run
public sealed class SingleInstanceGuard;                // named mutex + pipe activation
```

Settings live in `%LOCALAPPDATA%\Crowsnest\settings.json`; logs roll into `%LOCALAPPDATA%\Crowsnest\logs\` with seven-day retention via Serilog, plus an in-memory ring buffer sink so the diagnostics window can tail without touching disk.

The tray icon carries the whole status story: grey when nothing is connected, amber when one of sim or device is up, green when both are. Hovering shows which.

---

## 9. Firmware, board abstraction, and portability

### 9.0 Two questions, two layers

"Is there a library for displaying data on these panels?" is really two questions, and conflating them is the usual reason these projects end up unportable.

| Layer | Question | Answer |
|---|---|---|
| **Board HAL** | Who knows the pins, the panel driver, the touch controller, the backlight? | **ESP-BSP** (Espressif's Board Support Packages) |
| **UI toolkit** | Who draws anti-aliased numerals and handles dirty rectangles? | **LVGL 9** |

Both are off-the-shelf. What Crowsnest adds is a thin input shim and the UI itself.

### 9.1 The board layer: ESP-BSP

Espressif maintains [esp-bsp](https://github.com/espressif/esp-bsp), a repository of board support components exposing **a unified C API across every supported board** — `bsp_display_start()`, `bsp_display_lock()`, `bsp_display_backlight_on()`, button and I²C helpers — distributed through the IDF component registry. The stated goal is cross-board development from one codebase, which is exactly the requirement.

Critically for this project, **esp-bsp already ships a BSP for the M5Stack M5Dial** — a 1.28" round touchscreen with a rotary encoder and push button. That is the closest structural analogue to the CrowPanel, and it proves the abstraction extends to rotary dials rather than stopping at rectangular touch panels.

Two things to know before committing:

- **`esp_bsp_generic` will not work here.** The configure-by-menuconfig generic BSP supports SPI displays only — ST7789, ILI9341, GC9A01. The CrowPanel's ST7701 over 16-bit RGB parallel is outside its scope, so this board needs a real BSP component.
- **The ESP-BSP Generator solves that.** Espressif now publishes a web generator where you specify display driver, pin assignments, touch controller, buttons and other peripherals, and it emits a complete ESP-IDF component following esp-bsp conventions — CMakeLists, Kconfig, headers, sources — that you maintain as a small board package rather than a fork. That is the intended route for `bsp_crowpanel_21_rotary`.

The division of responsibility:

| Owned by the BSP | Owned by Crowsnest |
|---|---|
| Panel init sequence, RGB timings, framebuffer allocation | Screen layouts, fonts, theme |
| Touch controller init, LVGL indev registration | Gesture interpretation |
| Backlight and brightness | Brightness *policy* (day/night) |
| I²C bus setup, PCF8574 expander | Encoder and button semantics |
| `esp_lvgl_port` wiring, LVGL lock | Everything the user sees |

With this in place, `crowsnest_ui` includes `bsp/esp-bsp.h` and never mentions a pin number. Supporting the M5Dial becomes swapping one component dependency. Supporting a Waveshare or Viewe knob becomes generating one more BSP.

### 9.2 The gap: encoder handling

ESP-BSP's display, touch and button APIs are well established. Encoder support is the least uniform part across BSPs, and it is the one peripheral this project cannot do without. So Crowsnest defines a deliberately tiny shim — the single file a board port has to implement:

```c
// components/crowsnest_input/include/crowsnest_input.h
esp_err_t crowsnest_input_init(void);
int       crowsnest_encoder_read_detents(void);   // signed, consumed on read
bool      crowsnest_button_is_pressed(void);
```

On the CrowPanel this is backed by the ESP32-S3 PCNT peripheral with a glitch filter for the encoder on GPIO 42/4, and a 20 ms poll of PCF8574 P5 for the knob button. On the M5Dial it would be backed by that board's equivalents. Everything above the shim is identical.

> **Serialise I²C access behind one mutex.** There is an open esp-bsp issue where M5Dial encoder and button polling collides with LVGL's I²C touch reads and aborts the program. The CrowPanel has the same hazard by construction — its touch controller *and* its knob button both hang off the same I²C bus. Route every I²C transaction through a single mutex from day one rather than debugging sporadic aborts later.

### 9.3 Firmware structure

```
firmware/crowsnest-display/
├── components/
│   ├── bsp_crowpanel_21_rotary/      generated via the ESP-BSP Generator
│   ├── crowsnest_input/              encoder + button shim          ← per-board
│   ├── crowsnest_link/               framing, NDJSON, handshake     ← board-independent
│   └── crowsnest_ui/                 LVGL layouts, fonts, theme     ← board-independent
├── main/app_main.c
├── sdkconfig.defaults.crowpanel_21
└── sdkconfig.defaults.m5dial
```

**A board port is three artefacts:** one BSP component, one input shim, one sdkconfig defaults file. `crowsnest_link` and `crowsnest_ui` never change. That is the deliverable that makes "other people can use other rotary ESP32 dials" real rather than aspirational.

**Toolchain: ESP-IDF 6.1**, installed via the ESP-IDF Installation Manager (EIM). esp-bsp's compatibility table covers IDF 5.2 through 6.1 inclusive, so the BSP Generator route is unaffected by taking the current release rather than the 5.x line.

Two properties of EIM worth recording, because they shape how the firmware build is documented for anyone else picking this up:

- **The install is machine-level, not per-project.** EIM places each IDF version under a base path — `C:\esp\v6.1\esp-idf` on the reference machine, `C:\Espressif` being the tool's default — and registers it in `eim_idf.json`. Nothing lands in the repo, and there is no project-scoped install mode. A checkout therefore cannot carry its own toolchain; the version is a documented prerequisite, not a committed one.
- **Version selection is per-shell.** `eim select` sets the active version, `eim shell` opens a shell with one activated, and `eim run` executes a single command in that context. Multiple IDF versions coexist, so pinning to 6.1 does not foreclose testing a board port against an older line. `eim install --config-file-save-path <file>` writes a replayable install configuration — worth committing once the firmware tree exists, as the closest thing to a reproducible toolchain spec.

**Host prerequisite: USB-UART bridge drivers.** Windows ships no CH34x driver inbox, and §6 expects the CrowPanel to present a CH34x-class bridge rather than the ESP32-S3's native USB. Without the driver the board enumerates as an unknown device and never appears as a COM port, which fails both flashing and `SerialPortTransport`. `eim install-drivers` (or the equivalent button in the EIM GUI) installs the CP210x, CH34x and FTDI drivers in one step; it is machine-level and idempotent. Run it before the first board is plugged in, and record the port inventory beforehand — the new port that appears on connect is the panel, and its VID/PID settles the open question in §6 about which bridge Elecrow fitted.

> **Superseded for this board — see F4 in §9.6.** The reference CrowPanel enumerated inbox on Windows with no driver installed, because Elecrow wired USB-C to the ESP32-S3's native USB rather than a bridge. `eim install-drivers` remains worth running once on a development machine — board ports on other hardware, and any CP210x/CH34x programming adapter, still need it — but it is not a prerequisite for the CrowPanel.

Tasks:

| Task | Core | Responsibility |
|---|---|---|
| `lvgl_task` | 1 | `lv_timer_handler()` every 5 ms; the only task that touches LVGL |
| `link_rx_task` | 0 | UART read, frame split, parse, enqueue |
| `link_tx_task` | 0 | Drains the outbound queue |
| `input_task` | 0 | Polls `crowsnest_input`, emits detent and button events |

The firmware implements the three `PageLayout` variants and nothing else. It receives formatted strings with cursor spans and renders them. It has no concept of frequencies, altitudes, or units — which is precisely why adding NAV and autopilot support costs nothing on this side.

### 9.4 Why LVGL, and what was rejected

| Option | Verdict |
|---|---|
| **LVGL 9 + ESP-BSP + `esp_lvgl_port`** | **Recommended.** Font subsetting keeps a 96 px numeral set cheap; dirty-rectangle refresh keeps PSRAM bandwidth manageable on an RGB panel; arc and roller widgets suit the round bezel; SquareLine Studio can lay screens out visually |
| LVGL 8.3 + `Arduino_GFX` | Elecrow's own demo stack, including the working `st7701_type5_init_operations` sequence. **Use for the week-one spike**, then port |
| **openHASP** | Architecturally the closest prior art — LVGL firmware driven by JSONL from a host, which independently validates the thin-client design. Rejected on two counts: it is MQTT and Home Assistant shaped, so you would need a broker running beside the sim; and rotary encoder support has historically been absent, with maintainers stating there was none. Worth watching, not worth building on |
| **ESPHome** | Elecrow shipped official ESPHome configs for this board in early 2026, and working ESPHome + LVGL + encoder configurations exist for the M5Dial. Genuinely viable as a **community path** — someone could drive Crowsnest from ESPHome without compiling C. Document it as an alternative; do not make it the primary firmware, because the custom protocol and per-detent latency would be fighting the framework |
| `esp32-smartdisplay` | Same idea as ESP-BSP — board config as build flags, LVGL on top — but scoped to Sunton "cheap yellow display" boards on the Arduino framework. Wrong board family |
| LovyanGFX / Arduino_GFX | Driver abstraction only. No touch, button, or BSP story; Arduino-centric. Useful below LVGL, not a substitute for a BSP |
| TFT_eSPI | No. Built for SPI panels; no meaningful RGB-parallel support |
| Hand-rolled framebuffer | No. Glyph rasterisation, kerning and dirty-rectangle tracking are weeks of work for a worse result |

### 9.5 Hardware reference — CrowPanel 2.1"

| | |
|---|---|
| MCU | ESP32-S3R8, dual-core LX7 @ 240 MHz, 512 KB SRAM, **8 MB PSRAM**, 16 MB flash |
| Panel | 2.1" 480×480 round IPS, **ST7701** over 16-bit RGB parallel (RGB565) |
| Touch | CST8xx-series capacitive controller, I²C |
| Encoder | Quadrature on GPIO **42** (A) and **4** (B) |
| Knob button | **PCF8574 P5** at I²C `0x21`, input-pullup — no direct GPIO interrupt |
| I²C bus | SDA **38**, SCL **39** |
| Backlight | GPIO **6** |
| PCF8574 `0x21` | P0 touch reset · P2 touch INT · P3 LCD power · P4 LCD reset · P5 encoder button |

The RGB-parallel panel with its framebuffer in PSRAM is the configuration where Wi-Fi contention causes tearing, which is the practical argument behind the serial-first decision in D2.

### 9.6 Toolchain findings from the first build

Validated on the reference machine against **ESP-IDF v6.1** (EIM, `C:\esp\v6.1\esp-idf`) by configuring and building the stock `hello_world` example for `esp32s3`, probing the panel with `esptool chip-id`, and adding `lvgl/lvgl` as a managed dependency. Five findings change decisions elsewhere in this document.

**The stock `sdkconfig` is wrong for this board in four places.** A freshly generated `sdkconfig` is 2,325 lines, essentially all defaults. `sdkconfig.defaults.crowpanel_21` carries only the deltas:

| Setting | IDF default | CrowPanel value | Reason |
|---|---|---|---|
| `CONFIG_ESPTOOLPY_FLASHSIZE` | `"2MB"` | `"16MB"` | §9.5 |
| `CONFIG_PARTITION_TABLE_*` | `SINGLE_APP` | custom, two OTA slots | §6.3 |
| `CONFIG_SPIRAM` | absent (disabled) | enabled, octal mode | see framebuffer note below |
| `CONFIG_FREERTOS_HZ` | `100` | `1000` | see tick-rate note below |

**F1 — The FreeRTOS tick rate must be raised to 1 kHz.** IDF defaults to `CONFIG_FREERTOS_HZ=100`, a 10 ms tick. `vTaskDelay` cannot express a shorter interval than one tick, so the 5 ms `lv_timer_handler()` period in §9.3 silently becomes 10 ms — halving the UI refresh rate with no error and no warning. Setting `CONFIG_FREERTOS_HZ=1000` is standard practice for LVGL projects and belongs in both `sdkconfig.defaults` files, not just the CrowPanel one. The 20 ms `input_task` poll is unaffected either way.

**F2 — The debug console and the link may be contending for UART0.** IDF defaults to `CONFIG_ESP_CONSOLE_UART_DEFAULT`, which routes `printf` and the whole `ESP_LOG` family to UART0. §6 expects the CrowPanel to expose USB-C through a CH34x-class bridge; if that bridge is wired to UART0, then every log line the firmware emits is injected into the same byte stream as the NDJSON protocol frames. The failure presents as intermittent frame-parse errors on the host that correlate with nothing the host did, which is an expensive thing to debug from the C# side.

> **Narrowed by F4, not eliminated.** There is no bridge and no UART0 involvement, so the collision moves rather than disappears: USB-Serial/JTAG presents a *single* CDC endpoint, and routing both the console (`CONFIG_ESP_CONSOLE_USB_SERIAL_JTAG`) and the Crowsnest link through it interleaves log lines with NDJSON frames on the same wire. Three viable resolutions — console out to UART0 physical pins and read with a TTL adapter; a TinyUSB composite device exposing two CDC interfaces; or logging compiled out of release builds with `CONFIG_LOG_DEFAULT_LEVEL_NONE`. Decide before `crowsnest_link` is written; it is a config change now and a protocol-level mystery later.

**F3 — The firmware tree cannot live under the user profile on Windows.** Windows caps a full path at 260 characters, and the IDF build tree is deep — `build/esp-idf/<component>/CMakeFiles/__idf_<component>.dir/...`. Building `hello_world`, the smallest project that exists, from a path 118 characters long produced a CMake warning that object files were landing 199 characters in against a 250-character ceiling. The real firmware — LVGL 9, a generated BSP component, and nested component build directories — has a substantially deeper tree and less headroom.

The repository therefore moves out of the OneDrive profile path to a short root at `C:\projects\crowsnest`, with the firmware at `C:\projects\crowsnest\firmware\crowsnest-display`. Git is the backup mechanism; OneDrive sync was never appropriate for a tree that generates hundreds of megabytes of build output. This is a prerequisite for Phase 3, not a preference.

**F4 — The board uses native USB-Serial/JTAG, not a CH34x bridge.** `esptool chip-id` against the reference panel reports:

```
Chip type:   ESP32-S3 (QFN56) (revision v0.2)
Features:    Wi-Fi, BT 5 (LE), Dual Core + LP Core, 240MHz, Embedded PSRAM 8MB (AP_3v3)
USB mode:    USB-Serial/JTAG
MAC:         a4:cb:8f:dc:cc:6c
```

Windows enumerated it as `USB\VID_303A&PID_1001` with no driver installed — `303A` being Espressif's own vendor ID, against `1A86:7523` for a CH34x. Embedded 8 MB PSRAM confirms the `ESP32-S3R8` part in §9.5, and the MAC is the eFuse address §5 uses to identify panels. Consequences: the §6 discovery filter is `VID_303A&PID_1001`; baud rate is meaningless over a virtual CDC port; the driver prerequisite in §9.3 does not apply to this board; and F2 relocates from UART0 to the USB CDC endpoint.

**F5 — Component downloads fail on a cold CDN edge, and the manager does not retry.** Adding `lvgl/lvgl: ^9.0.0` to a manifest failed twice with `ERROR: Cannot download component lvgl/lvgl@9.6.0~1. ('Connection broken:`. The cause is not local: no proxy is configured, and the registry API answered 200 in 1.6 s throughout. The LVGL package is **110 MB**, and it was cold in the nearest CloudFront edge — the first request carried `X-Cache: Miss from cloudfront`. On a miss CloudFront streams from the S3 origin while filling its cache, which measured 840 KB/s here, and the connection was severed 5.5 MB in. The IDF Component Manager performs **no retry and no range-resume**, so a single dropped connection fails the whole configure step.

Completing the transfer once with `curl -C - --retry` warmed the edge; subsequent requests returned `X-Cache: Hit from cloudfront` at 8 MB/s, and an unaided `idf.py reconfigure` then downloaded and extracted the component normally. Expect this on any fresh machine, any CI runner, and any new region — `esp-bsp` and the LVGL port are in the same size class.

Three rules follow:

- **Retry before investigating.** A failed fetch partially warms the edge, so a second `idf.py build` frequently succeeds unaided. This one would have.
- **Commit `dependencies.lock`; ignore `managed_components/`.** The lock pins exact resolved versions so a CI runner builds what the developer built. The directory it populates is 201 MB for LVGL alone and must never enter the repository.
- **Keep a manual recovery path.** Verified working, and the answer for an offline or air-gapped build:

```bash
# 1. fetch the archive with resume enabled (url is in dependencies.lock)
curl -C - --retry 40 --retry-all-errors -o lvgl.zip   https://components-file.espressif.com/components/lvgl/lvgl/<ver>/<file>.zip
# 2. extract to managed_components/<namespace>__<name>/
# 3. write the component_hash from dependencies.lock into .component_hash
printf '<hash-from-dependencies.lock>' > managed_components/lvgl__lvgl/.component_hash
```

The manager validates the extracted tree against `.component_hash` and, on a match, skips the download entirely.

**Framebuffer arithmetic, for the record.** A 480×480 RGB565 framebuffer is 460,800 bytes. The ESP32-S3 has 512 KB of internal SRAM in total, shared between framebuffers, task stacks, the LVGL heap and the link buffers. A single framebuffer does not fit, let alone the double-buffering an RGB-parallel panel wants. The framebuffer must be allocated from the 8 MB PSRAM, which is what makes the Wi-Fi bandwidth contention in §9.5 a structural property of this board rather than a tuning problem — and therefore what makes D2's serial-first decision a memory-bandwidth argument as much as a latency one.

## 10. Design patterns in use

| Pattern | Where | Why |
|---|---|---|
| Ports and Adapters | `Core` defines `ISimParameterGateway` / `IPanelDevice`; `Sim` and `Device` implement them | Domain logic is testable without the sim or the hardware, which are the two things you cannot put in CI |
| **Registry / data-driven config** | `ParameterRegistry` loaded from embedded JSON | Adding NAV, COM 2, or the autopilot becomes content, not code. This is the single most important pattern for the extension roadmap |
| **Module / contribution** | `IPanelModule` + `PanelBuilder`, registered in `PanelCatalog` | A panel is a vertical slice that contributes parameters, pages, formatters and behaviour through one seam. Feature co-location without assembly sprawl |
| Strategy | `IValueGrid`, `IEncoderAcceleration`, `IDeviceTransport`, `IValueFormatter` | A frequency, an altitude and a squawk code differ only in which strategy they carry |
| Value Object | `ComFrequency`, canonical-int values | Integer canonical units remove a whole class of rounding bug |
| Flyweight | Grid instances cached per configuration | Two `ComChannelGrid` instances, not one per parameter |
| State Machine | `TuningSession` | Pending/confirmed reconciliation is genuinely stateful; explicit makes it testable |
| Mediator | `PanelCoordinator` | Sim and device never reference each other |
| Command | `BridgeCommand` + `IInputActionMap` | Gesture rebinding becomes configuration |
| **Capability negotiation** | `DeviceCapabilities` from the `hello` handshake | The host adapts its layout to whatever board is plugged in, rather than the firmware adapting to the host |
| Decorator | `SimConnectSupervisor` wrapping `SimConnectClient` | Reconnection logic stays out of connection logic |
| Builder | `DataDefinitionBuilder`, `BindingCompiler` | SimConnect data definitions are order-dependent and easy to get wrong imperatively |
| Adapter | `SimConnectParameterGateway`, `PanelDeviceConnection`, the BSP shim | Translate between the domain and three unpleasant external APIs |
| Producer/Consumer | `Channel<BridgeEvent>` | One event loop, no locks in `Core` |
| Null Object | `FakeParameterGateway`, `LoopbackTransport` | Develop and demo the whole system with neither sim nor hardware present |

## 11. Testing

| Layer | Approach |
|---|---|
| `IValueGrid` implementations | Golden tests for all 16 8.33 kHz channels per 100 kHz block and all 4 in 25 kHz mode. Property tests (FsCheck) run against **every** grid: every result is legal and in range; *n* steps forward then *n* back is the identity; `WrappingGrid` wraps 359→0 and back |
| `TuningSession` | `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`. Assert debounce, forced-write interval, confirmation, and settle-timeout rejection deterministically |
| `PanelCoordinator` | `FakeParameterGateway` + `LoopbackTransport` + fake clock. Full scenario tests with no I/O, including page switching and capability-driven layout selection |
| `ParameterRegistry` | Every embedded JSON entry round-trips and validates: cursor spans fall inside the formatted string, grid ranges are non-empty, every `ReadBinding` has a matching `WriteBinding` |
| **Every `IPanelModule`** | A shared `PanelModuleContract` theory runs against `PanelCatalog.All`: ids unique, `Order` unique, every page field resolves to a registered parameter, every referenced formatter and grid key exists, every declared `SwapEvent` is non-empty on paired layouts. **Adding a panel inherits this suite automatically** |
| Architecture | NetArchTest or similar: `Domain/` and `Application/` must not reference `Panels/`; no `Panels/X` may reference `Panels/Y` |
| Protocol codec | Round-trip tests plus a fuzz test against truncated frames, split frames, garbage bytes, and unknown message types |
| `Crowsnest.SimConnect` | Not unit-testable. `tools/Crowsnest.DevConsole` is the manual harness; keep it in the repo |
| Firmware | `tools/Crowsnest.DeviceSimulator` speaks the device protocol from a desktop app, so the PC side is fully developable before firmware exists — and the real device can be swapped in to isolate which side broke. The firmware's own test strategy is §11.1 |


### 11.1 Firmware testing

The argument in the table above stops at the C# boundary, which leaves the firmware as the one tier with no automated coverage. ESP-IDF supplies the missing pieces; they are worth adopting from the first commit rather than retrofitting.

**Three tiers, split by what actually needs silicon.**

| Tier | Mechanism | Covers |
|---|---|---|
| **Host unit tests** | Unity on the IDF `linux` target | `crowsnest_link` in full; any logic in `crowsnest_ui` that is not an LVGL call |
| **Hardware smoke test** | `pytest-embedded` (`dut.expect`) | Boot, `hello` handshake, one rendered frame |
| **Manual** | `idf.py monitor` + `Crowsnest.DeviceSimulator` | Encoder feel, panel init, latency |

**`crowsnest_link` is pure logic and belongs entirely in tier one.** Framing, NDJSON parse, the `hello` handshake and the outbound event encoder touch no peripheral. ESP-IDF ships `components/unity/port/linux`, so a `linux`-target build compiles these as an ordinary native binary and runs them in milliseconds. This is the firmware mirror of the `Protocol codec` row above, and the two should **share one corpus of malformed frames** committed to the repository — truncated frames, split frames, garbage bytes, unknown message types — exercised by the C# fuzz test and the Unity host test alike. A protocol defect that only one end rejects is precisely the bug this arrangement catches.

> **Windows constraint:** the IDF `linux` target requires POSIX APIs and does not build natively on Windows. Host tests run under WSL. This is the only part of the firmware toolchain that does, and it is a reason to keep `crowsnest_link` free of IDF dependencies beyond `esp_err_t` — the less it needs, the more portable its test harness.

**What cannot be host-tested, and should not be faked.** `crowsnest_input` is the PCNT peripheral and an I²C expander read; `bsp_crowpanel_21_rotary` is a panel init sequence and RGB timings. Mocking either tests the mock. These are covered by the tier-two smoke test and by hand — which is acceptable precisely because they are the two components a board port is expected to replace anyway.

**The smoke test earns its place at the OTA boundary.** A `pytest-embedded` test that flashes a build, waits for the `hello` frame and asserts on `DeviceCapabilities` is the cheapest possible guard against shipping an image that boots but never speaks. Given §6.3 pushes firmware over the same link the test uses, that guard runs against the exact path a user's update takes.


---

## 12. Installer

WiX v5 producing a per-machine MSI.

- Installs to `%ProgramFiles%\Crowsnest\`
- Full Add/Remove Programs metadata: `ProductName`, `Manufacturer`, `ProductVersion`, `ARPHELPLINK`, `ARPPRODUCTICON`, `ARPNOMODIFY` — this is the "appears in Control Panel" requirement
- Publishes self-contained `win-x64` so there is no .NET runtime prerequisite; `SimConnect.dll` ships alongside as x64
- Autostart via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, written per-user on first launch rather than by the MSI, so it survives a per-machine install cleanly
- Upgrade code fixed across versions; major-upgrade sequencing so reinstalls replace rather than duplicate
- **Code signing.** An unsigned MSI will trip SmartScreen and generate support noise. Azure Trusted Signing is currently the cheapest route to a reputation-building certificate

Firmware flashing stays out of the installer for v1 — ship the `.bin` and a `esptool` script separately.

---

## 13. Delivery phases

| Phase | Deliverable | Exit criterion |
|---|---|---|
| **0 — Spikes** (~1 week) | Four throwaway projects, run in parallel | (a) a .NET 10 console app reads `COM ACTIVE FREQUENCY:1` and writes `COM_STBY_RADIO_SET_HZ`; (b) LVGL draws a frequency on the CrowPanel via Arduino_GFX; (c) measured USB CDC round-trip latency under 20 ms; (d) the ESP-BSP Generator produces a BSP that builds and lights the panel |
| **1 — Core** | `Crowsnest.Core` + `Crowsnest.Device` + `DeviceSimulator` | Full tuning behaviour demonstrable against a fake sim and simulated device, with a green test suite. Registry contains COM 1 **and NAV 1**, even though only COM 1 ships — proving the generalisation before it is needed |
| **2 — Sim** | `Crowsnest.SimConnect` + `Crowsnest.Sim` | Tuning works end to end against a real sim, driven by the device simulator |
| **3 — Firmware** | `bsp_crowpanel_21_rotary` + `crowsnest_input` + `crowsnest_ui` | Real hardware replaces the simulator with no change to the PC side |
| **4 — Ship v1** | `Crowsnest.Tray` + `Crowsnest.Installer` | Signed MSI installs, autostarts, appears in Add/Remove Programs, survives sim restart and cable unplug |
| **5 — Radios** | NAV 1, COM 2, NAV 2, page navigation | One new `NavPanelModule` + one line in `PanelCatalog`. **If this phase requires changes outside `Panels/Nav/`, the §5 abstraction failed and should be revisited before phase 6** |
| **6 — Autopilot** | Altitude, heading, vertical speed, airspeed | Exercises `SignedLinearGrid`, `WrappingGrid`, and three-level cursors |
| **6.5 — Multi-panel** | `IPanelDeviceManager`, role assignment UI, OTA update flow | Three panels on one hub, each assigned different pages, sharing tuning state; all three update from one tray action |
| **7 — Portability** | Second board port + Wi-Fi transport | A board Crowsnest was not designed against runs the unmodified `crowsnest_ui` |

Phase 0 exists because all four of the project's real unknowns are in it. Phase 5 is deliberately positioned as a **test of the architecture** rather than just a feature: if adding NAV 1 is not nearly free, that is worth knowing before the autopilot work starts.

**0(a) — passed 2026-09-24.** `Crowsnest.SimSpike` against MSFS 2024 (`SunRise 12.2 build 282174.999`, SimConnect 12.2), default GA aircraft on the ground, using the SDK's managed wrapper under .NET 10. `COM ACTIVE FREQUENCY:1` / `COM STANDBY FREQUENCY:1` read as `Hz` / `FLOAT64` via `RequestDataOnSimObject` at `VISUAL_FRAME` with `CHANGED`; `COM_STBY_RADIO_SET_HZ` written with `TransmitClientEvent` at highest group priority. **Write to read-back: 9.8 ms and 16.3 ms** (write, then restore) — about one visual frame, so the pending/confirmed window in `TuningSession` will be short in practice. Cockpit knob turns arrive as individual change notifications at 25 kHz steps. The console loop works with an `EventWaitHandle` and no window handle, so `Crowsnest.Sim` needs no hidden message window. `COM_STBY_RADIO_SWAP` also verified: active and standby exchange in a single change notification, and a manual standby write read back in 12.0 ms. Not yet exercised: 8.33 kHz spacing and a study-level aircraft.

**0(c) — passed 2026-09-24.** `Crowsnest.LinkSpike` against the reference panel (fw 0.1.0, COM4), 500 pings after a 10-ping warm-up: **min 0.37 ms, median 0.98 ms, p95 1.05 ms, max 1.32 ms** — roughly 20× inside the budget. Three further back-to-back runs without resetting the panel all reconnected and stayed under 1 ms median, so a host restart does not strand the panel. Two observations from the session:

- **Opening the port with DTR and RTS both asserted resets the chip, and the wrong sequence leaves it in the ROM bootloader** (`boot:0x0 (DOWNLOAD(USB/UART0))`, `waiting for download`) — silent to the link and indistinguishable from dead firmware. Stock serial monitors do this. Recovery is an RTS pulse with DTR low (esptool's hard reset). This is the same hazard `SerialPortTransport` guards against; it applies equally to every ad-hoc tool pointed at the port.
- **Open: the panel was silent at the start of the session** — it enumerated but ignored the host hello after sitting connected overnight, and a reset cleared it. Not reproduced. Suspects are USB suspend across PC sleep leaving the USB-Serial/JTAG driver wedged, or a firmware hang after long uptime. Worth a soak test before phase 4, since "survives cable unplug" is an exit criterion and this looks like its neighbour.

**Phase 0 prerequisites.** Three machine-level installs gate the spikes, none of them project-scoped, all worth doing before the week starts rather than during it:

| Prerequisite | Gates | Note |
|---|---|---|
| **MSFS 2024 SDK** | 0(a) | Provides `SimConnect.dll` (native, x64) and `Microsoft.FlightSimulator.SimConnect.dll` (managed wrapper) under `%MSFS2024_SDK%\SimConnect SDK\lib\`. Vendor both into the repo rather than referencing the SDK path, so the build is not machine-specific |
| **ESP-IDF 6.1 via EIM** | 0(b), 0(d) | See §9.3. Machine-level; the repo carries the install configuration, not the toolchain |
| **USB-UART bridge drivers** | 0(b), 0(c) | `eim install-drivers`. **This is the one that silently blocks the hardware spikes** — with no CH34x driver the panel does not enumerate as a COM port, and the failure looks like a dead board rather than a missing driver. Do it before first plug-in |

The driver step is called out separately because it is the only prerequisite whose absence produces a misleading symptom. The other two fail loudly at build time.

## 14. Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| **SimConnect interop under .NET 10** — *retired* | — | — | **Fully retired 2026-09-24** by spike 0(a) (§13): a live session against MSFS 2024 reads COM 1 and writes standby with ~10–16 ms read-back through the managed wrapper. **Partly retired 2026-09-19** against SDK 12.2.0.0. Native `SimConnect.dll` loads with every required export resolving. The managed wrapper is mixed-mode (`NativeEntryPoint`, references `mscorlib 4.0.0.0`) and yet compiles against and loads under .NET 10.0.11, reaching native `SimConnect_Open` — `0x80004005` with no sim running, i.e. the call lands. §7.1's doubt about the wrapper is therefore resolved in its favour. The session path — data definitions, the dispatch loop and a real read/write — was the residual, and 0(a) closed it. Keep `ISimConnectClient` regardless — it costs nothing and the P/Invoke client is still the target |
| **Study-level aircraft ignore standard key events** (documented for A2A, PMDG, and the FBW A380X, whose standby SimVars diverge from its displayed frequencies) | High | Medium | The settle-timeout path in `TuningSession` degrades gracefully and surfaces `aircraft_rejected` rather than appearing frozen. `ReadSource.LVar` / `WriteMode.LVarWrite` reserve the escape hatch |
| **Autopilot values are written by the sim continuously** (VNAV stepping altitude, LNAV moving the heading bug) | Certain | Medium | This is normal operation for AP parameters, not an edge case. The pending/confirmed model already handles it; budget extra testing time in phase 6 rather than extra design |
| **The registry over-generalises** and becomes a config language nobody can debug | Medium | Medium | Keep it internal and non-user-editable in v1. Every entry covered by a round-trip test. Phase 5 is the checkpoint — if NAV 1 is not nearly free, simplify rather than adding more indirection |
| In-sim ATC auto-tuning silently reverts a frequency the user just set | Medium | Medium | Documented sim behaviour, not a bug. Detect the pattern and surface it as a notice |
| **ESP-BSP has no BSP for this board** and `esp_bsp_generic` covers SPI displays only | Certain | Low | Known and planned for: the ESP-BSP Generator produces the component. Validated in phase 0(d) before any UI work depends on it |
| **I²C contention between touch reads and knob-button polling** — an open esp-bsp issue causes aborts on the M5Dial, and the CrowPanel shares the hazard | Medium | Medium | Single I²C mutex from day one |
| RGB panel tearing under Wi-Fi load | Medium | Low in v1 | v1 is serial-only; measure before committing to Wi-Fi in phase 7 |
| `SimConnect.dll` redistribution terms | Low | High if wrong | Read the MSFS SDK licence before bundling. Fallback is to require an SDK install or locate the DLL at runtime |
| **Hub cannot supply 5 A** — bus-powered hubs give 500–900 mA/port and many "powered" hubs ship a 2 A adapter | High if unspecified | High — brown-outs mid-flash | Specify a 40 W+ hub with ≥1 A per port; never OTA more than one panel at a time |
| **Windows renumbers COM ports** across reboots with five identical VID/PID devices | Certain | High if port-keyed | Identity is the eFuse MAC from the `hello` frame; nothing keys off a port name |
| esptool's GPL-2.0 licence versus a closed-source installer | Medium | Medium | OTA over the existing link avoids the dependency; resolve before adopting esptool |
| Unsigned installer trips SmartScreen | High | Low | Budget for a signing certificate before public release |

## Appendix A — SimConnect reference

**SimVars** (all readable; frequency vars are not directly settable, hence the key events)

```
COM ACTIVE FREQUENCY:1     MHz  (FLOAT64)   — read as MHz, round to kHz
COM STANDBY FREQUENCY:1    MHz  (FLOAT64)
COM SPACING MODE:1         enum            — 25 kHz vs 8.33 kHz
COM STATUS:1               enum
```

**Key events**

```
COM_RADIO_SET_HZ           Hz   — COM1 active   (note: no COM1_RADIO_SET_HZ exists)
COM_STBY_RADIO_SET_HZ      Hz   — COM1 standby
COM_STBY_RADIO_SWAP        0    — swap COM1
COM1_RADIO_SWAP            0    — alias
COM2_RADIO_SET_HZ          Hz
COM2_STBY_RADIO_SET_HZ     Hz
COM_1_SPACING_MODE_SWITCH  0
```

**8.33 kHz valid fractions per 100 kHz block**

```
.000 .005 .010 .015   .025 .030 .035 .040
.050 .055 .060 .065   .075 .080 .085 .090

omitted: .020 .045 .070 .095
```

**25 kHz valid fractions per 100 kHz block**

```
.000 .025 .050 .075
```

## Appendix B — Board pin reference

```
I2C_SDA              GPIO 38
I2C_SCL              GPIO 39
ENCODER_A            GPIO 42
ENCODER_B            GPIO 4
BACKLIGHT            GPIO 6

PCF8574 @ 0x21
  P0  touch reset
  P2  touch interrupt
  P3  LCD power
  P4  LCD reset
  P5  encoder button (INPUT_PULLUP)

ST7701 RGB565 parallel
  CS 16 · SCK 2 · SDA 1
  DE 40 · VSYNC 7 · HSYNC 15 · PCLK 41
  R0-R4  46, 3, 8, 18, 17
  G0-G5  14, 13, 12, 11, 10, 9
  B0-B4  5, 45, 48, 47, 21
```

Timings from Elecrow's reference: hsync front porch 10, pulse width 4, back porch 20; vsync front porch 10, pulse width 4, back porch 20; BGR order; `st7701_type5_init_operations`.
