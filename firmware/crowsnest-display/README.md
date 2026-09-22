# crowsnest-display

Panel firmware. ESP-IDF 6.1 + LVGL 9.

A thin client: it renders pre-formatted text the host sends and reports what the knob and
the screen did. It holds no tuning state, knows no units, and has never heard of a radio.
Adding NAV, COM 2 or the entire autopilot requires no change here — see §5.6 of the
technical spec for why that is the pivotal design decision.

## Layout

```
components/
  bsp_crowpanel_21_rotary/   board HAL: I²C, ST7701 init, RGB panel, backlight   ← per-board
  crowsnest_input/           encoder + knob button shim                          ← per-board
  crowsnest_link/            NDJSON framing, parsing, encoding                    board-independent
  crowsnest_ui/              LVGL layouts, fonts, theme                           board-independent
main/app_main.c              task wiring
```

**A board port is three artefacts**: one BSP component, one `crowsnest_input`
implementation, one `sdkconfig.defaults.<board>`. `crowsnest_link` and `crowsnest_ui`
never change. That is what makes "other rotary ESP32 dials can run this" real rather than
aspirational.

## Prerequisites

ESP-IDF 6.1, installed machine-level via EIM. Nothing lands in the repo; the version is a
documented prerequisite, not a committed one. On the reference machine:

```powershell
. C:\Espressif\tools\Microsoft.v6.1.PowerShell_profile.ps1
```

EIM's activation profile lives under `IDF_TOOLS_PATH`, not beside the IDF checkout, and
`C:\esp\v6.1\esp-idf\export.ps1` will **not** work on its own — it looks for a Python
environment at the stock `~/.espressif` path that EIM does not create.

## Build and flash

```
idf.py set-target esp32s3
idf.py build
idf.py -p COM4 flash
```

`CROWSNEST_BOARD` selects the board's sdkconfig overlay, defaulting to `crowpanel_21`:

```
idf.py -DCROWSNEST_BOARD=m5dial build
```

### The console is not on USB

`idf.py monitor` shows **nothing** over the USB cable, by design.

The ESP32-S3's native USB-Serial/JTAG presents a single CDC endpoint. Routing both the
ESP_LOG console and the Crowsnest link through it interleaves log lines with NDJSON frames
on the same wire, and the symptom is intermittent frame-parse errors on the host that
correlate with nothing the host did — an expensive thing to debug from the C# side
(spec §9.6 F2). Crowsnest resolves this by giving the link the USB endpoint exclusively
and sending the console out the physical UART0 pins.

Two ways to read logs:

- A 3.3 V TTL adapter on the UART0 header. This is the one that works while the link is up.
- The development overlay, which puts the console back on USB and re-creates the collision:

  ```
  idf.py -DSDKCONFIG_DEFAULTS="sdkconfig.defaults;sdkconfig.defaults.crowpanel_21;sdkconfig.defaults.devconsole" build
  ```

  Use it for panel bring-up, never while debugging the link.

The firmware can also send log lines to the host as `{"t":"log"}` frames, which is the
right channel for anything the user might need to see.

### First fetch may fail once

LVGL is 110 MB and the IDF Component Manager does no retry and no range-resume, so a cold
CloudFront edge drops the connection partway through. **Run the build again** — the failed
attempt warms the edge. The manual `curl -C -` recovery path is in spec §9.6 F5.

`dependencies.lock` is committed; `managed_components/` is 201 MB and is gitignored.

## Testing

| Tier | Mechanism | Covers |
|---|---|---|
| Host unit tests | Unity on the IDF `linux` target | `crowsnest_link` in full |
| Hardware smoke test | `pytest-embedded` | Boot, `hello` handshake, one rendered frame |
| Manual | `Crowsnest.DeviceSimulator`, `Crowsnest.LinkSpike` | Encoder feel, panel init, latency |

`crowsnest_link` is pure logic and belongs entirely in tier one — it depends on nothing but
libc precisely so the `linux` target can build it. **The IDF `linux` target needs POSIX and
does not build on Windows; host tests run under WSL.**

The malformed-frame corpus at `tests/protocol-corpus/malformed.ndjson` is shared with the
C# fuzz test in `Crowsnest.Device.Tests`. A frame that only one end rejects is exactly the
defect that arrangement exists to catch, so both suites must keep reading the same file.

## Notes for the next person

- **The ST7701 init sequence is a transcription, not a design.** It is Elecrow's
  `st7701_type5_init_operations`, and one wrong byte gives a blank or scrambled panel. Do
  not tidy it.
- **If red and blue are swapped**, change MADCTL (`0x36`) from `0x08` to `0x00` in
  `st7701_run_init_sequence()`. That bit selects BGR.
- **Every I²C transaction goes through `bsp_i2c_lock`.** The touch controller and the knob
  button share one bus and are polled from different tasks; there is an open esp-bsp issue
  where the equivalent collision aborts the M5Dial (spec §9.2).
- **Touch is not wired up yet.** The panel reports `touch: false` in its hello, so the host
  will not send it a page whose only swap gesture is a tap. The CST8xx driver is the next
  piece of the BSP.
