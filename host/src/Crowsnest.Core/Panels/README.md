# Panels

Each folder here is one **panel module**: a family of radios or instruments (COM, NAV,
transponder) and everything the host needs to drive it. Adding a panel means adding a folder
and one line in `PanelCatalog.cs`. You shouldn't need to change anything else, and the tests
fail if you do.

(Words: a *device* is the physical board, a *panel* is a family like COM, and a *page* is one
screen of it, like COM1.)

## Adding a panel, step by step

Say you're adding the ADF.

1. **Make the folder** `Panels/Adf/`.

2. **Describe the values** in `Panels/Adf/adf.parameters.json`. Each entry says how to read the
   value from the sim, how to write it back, which values are legal (the *grid*), how the knob
   moves through it (the *cursors*), and how to show it. Copy an entry from
   `Com/com.parameters.json` or `Nav/nav.parameters.json` and change it. Every id must start
   with your panel id: `adf1.standby`, `adf1.active`.

   Standard grids: `linear`, `wrapping`, `signedLinear`. Standard formats: `freq3`, `freq2`,
   `thousands`, `signedThousands`, `deg3`, `code4`.

3. **Describe the pages** in `Panels/Adf/View/adf.view.json`, one page per screen:

   ```json
   { "pages": [
       { "id": "adf1", "title": "ADF", "layout": "pair",
         "fields": [ "adf1.standby", "adf1.active" ], "swapEvent": "ADF1_RADIO_SWAP" } ] }
   ```

   The layout is one the firmware already draws: `pair` (active over standby), `single` or
   `dual`. The first field is the one the knob tunes. A `swapEvent` needs exactly two fields.

4. **Give the demo some values** (optional) in `Panels/Adf/adf.demo.json`, e.g.
   `{ "adf1.active": 350, "adf1.standby": 410 }`. The fake sim and the device simulator start
   from these. Anything left out starts at the bottom of its grid.

5. **Write the module class** `Panels/Adf/AdfPanelModule.cs`. Its namespace must be the
   folder's, because that is how it finds its JSON:

   ```csharp
   using Crowsnest.Core.Application.Panels;

   namespace Crowsnest.Core.Panels.Adf;

   public sealed class AdfPanelModule : IPanelModule
   {
       public string Id => "adf";

       public void Configure(PanelBuilder panel)
       {
       }
   }
   ```

6. **Register it**: add `new AdfPanelModule(),` to `PanelCatalog.All`. Its position there is
   its place in the page order.

7. **Run the tests** (`dotnet test` in `host/`). `PanelCatalogTests` loads every registered
   module on its own and checks the rules below, so a typo shows up there with the panel and
   file named, not on the first turn of a knob.

## When JSON isn't enough

`Configure` is where the code goes. Everything you add there is private to your panel:

- `panel.AddGridType("adfBand", spec => ...)`: a grid with rules no standard one has. Your
  JSON names it in `"grid": { "type": "adfBand", ... }` and reads its own fields from `spec`.
  See `Com/ComSpacingBehaviour.cs`.
- `panel.AddFormatter("adfKhz", new MyFormatter())`: a way of showing a value.
- `panel.AddBehaviour(new MyBehaviour())`: logic that reacts to the sim, such as COM's grid
  following the aircraft's 8.33 kHz switch. It sees every sim snapshot and can replace a grid;
  `Validate` lets it check the finished registry at startup.

Create anything that keeps state inside `Configure`, not in a field, so each load gets its own.

Tests for that code go in the matching folder, `host/tests/Crowsnest.Core.Tests/Panels/Adf/`.

## The rules (enforced by tests)

- Parameter, watch and page ids start with the panel id.
- A page shows only its own panel's parameters.
- A panel uses only the standard grids and formats, plus its own.
- Code in a panel folder never refers to another panel's folder, and nothing outside
  `Panels/` (apart from `PanelCatalog`) refers to a panel. If you find yourself editing the
  engine, the sim gateway or the host for your panel, stop and ask: the module is probably
  missing a hook.
- The only files in a panel folder are `*.parameters.json`, `*.view.json`, `*.demo.json` and
  C#.

## What a panel can't do on its own

- **A new screen layout** is firmware work (spec §5.5): the firmware draws a small, fixed set
  of layouts, and pages pick one. Use `pair`, `single` or `dual` if you can.
- **Behaviour in the fake sim** beyond swaps and starting values (say, the sim snapping
  values when a mode changes) belongs in the tool that wants it, via `FakeParameterGateway.On`.
  The device simulator does this for COM spacing.
