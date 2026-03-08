# CCTVCapture CCTV System

A real-time ASCII CCTV system for Space Engineers that streams true-color video from a second SE client to in-game LCD panels using SE's hidden 0xE100 font palette.

---

## Prerequisites

- **Torch** dedicated server
- A second Steam account to run the fake client SE instance (no admin or special permissions required)

---

## Components

### 1. Torch Plugin — `CCTVPlugin.dll`
Server-side. Manages camera scanning, multiplayer GOTO messages, frame routing and LCD writes.
Install to: `Torch/Plugins/CCTVPlugin/`

### 2. `CCTVCapture.exe`
External application. Captures the SE window, converts frames to color ASCII and streams them to the plugin over TCP.
Run from: `CCTVCapture/bin/Release/net48/CCTVCapture.exe`

Accepts command-line arguments for multi-client setups:
```
CCTVCapture.exe --port 12346 --host 192.168.1.100
```

### 3. CCTV Spectator Controller — Client-Side Mod
Receives multiplayer GOTO messages from the plugin and moves the spectator camera directly using the local SE API. No character required — the fake client runs in pure spectator mode.
Steam Workshop: **[CCTV Spectator Controller](https://steamcommunity.com/sharedfiles/filedetails/?id=3670758606)**
Manual install: `%AppData%\SpaceEngineers\Mods\`

---

## Quick Start

### 1. Install the Torch plugin
Copy `CCTVPlugin.dll` to `Torch/Plugins/` and restart Torch.

### 2. Install the client-side mod
**Option A — Steam Workshop (recommended):**
Subscribe to the mod on the Steam Workshop: **[CCTV Spectator Controller](https://steamcommunity.com/sharedfiles/filedetails/?id=3670758606)**
Then enable it in world settings. The mod must be active on the fake client's SE instance.

**Option B — Manual install:**
Copy the `CCTVMod` folder to `%AppData%\SpaceEngineers\Mods\` and enable it in world settings.

### 3. Name your cameras and LCDs
Camera and LCD names follow a prefix + base-name pattern:

| Camera name | Single LCD | 2×2 Grid LCDs |
|---|---|---|
| `LCD_TVCamera Test01` | `LCD_TV Test01` | `LCD_TV Test01_TL/TR/BL/BR` |
| `LCD_TVCamera Hangar` | `LCD_TV Hangar` | `LCD_TV Hangar_TL/TR/BL/BR` |

Slave LCDs (copies of a master) follow the same naming pattern for both types:

| Master | Slave examples |
|---|---|
| `LCD_TV Test01` | `LCD_TV Test01_Slave`, `LCD_TV Test01_Slave2` |
| `LCD_TV Test01_TL` | `LCD_TV Test01_TL_Slave`, `LCD_TV Test01_TL_Slave2` |

> **Antenna required for slave LCDs:** A slave LCD's grid must have at least one active, broadcasting radio antenna. Grids without a powered antenna are skipped automatically at rescan time.

### 4. Run the fake client
1. Launch SE on the fake account in **windowed mode**
2. Connect to the server and press **F8** (spectator mode)
3. Run `CCTVCapture.exe`

The plugin will detect cameras, send GOTO messages and begin cycling. LCDs update automatically.

---

## Button Panel Control

The mod registers three terminal actions that appear in every Button Panel's G-menu action picker:

| Action | Effect |
|--------|--------|
| `CCTV: Next Camera` | Advance to the next camera in the cycle |
| `CCTV: Prev Camera` | Go back to the previous camera |
| `CCTV: Reset Cycle` | Restart the auto-cycle timer without switching camera |
| `CCTV: Next Loop` | Switch to the next camera loop (_L1 → _L2 → ...) |
| `CCTV: Prev Loop` | Switch to the previous camera loop |

### Setup

1. Place a **Button Panel** block in-game
2. Open its terminal and set **Custom Data** to the `LiveFeedLcdName` of the feed you want to control — e.g. `Test01`
3. Open the G-menu (**G** key), select the button panel, click a button slot → **Pick Action** → choose one of the three `CCTV:` actions
4. Press the button in-game — the plugin receives the command and switches the camera immediately

> **One button panel can control one feed.** The `CustomData` value is sent with every button press to identify which `CCTVCapture` instance to target. To control multiple feeds, use separate button panels each with a different `CustomData` value matching the corresponding `LiveFeedLcdName`.

> **The mod must be enabled in world settings** for the actions to appear in the G-menu picker. The client-side mod handles G-menu display; the server-side mod handles button execution — both sides register automatically.

⚠️ Important: Put the LCD name (e.g. Test01) in the Button Panel's CustomData only — never in the cockpit's CustomData. Setting it on the cockpit can cause client desync (no tools, frozen streams, unable to exit seats).
---

## Camera Loops

Camera loops let you group cameras into named sets that share a single set of LCDs. Pressing **Next Loop** / **Prev Loop** switches which group is cycling — the same master LCD immediately starts showing the new loop's feed with no stale image.

### Naming convention

Append `_L1`, `_L2`, etc. to both the camera name **and** nothing else — the LCD name stays unchanged:

| Camera block name | Loop |
|---|---|
| `LCD_TVCamera Test01_L1` | Loop 1 |
| `LCD_TVCamera Test01_L2` | Loop 2 |
| `LCD_TV Test01` | **Master LCD (shared by all loops)** |

Slave LCDs follow the same rule as always — they are slaves of the master LCD, not of any specific loop.

### Setup

1. Name your cameras with `_L1` / `_L2` suffixes as above
2. Keep `CameraSuffix=Test01` in the instance config — the plugin automatically matches all loop variants
3. Keep `LiveFeedLcdName=Test01` unchanged — all loops write to the same LCD
4. Assign **CCTV: Next Loop** and/or **CCTV: Prev Loop** to button panel slots (same `CustomData` as the camera buttons)
5. Press the button — the LCD switches to the new loop's cameras on the very next frame

> **Backwards compatible:** instances with no `_L{n}` suffixes on any camera have a single loop — Next/Prev Loop are silent no-ops.

---

## Vehicle HUD Mode

When a feed LCD is placed on a **non-static (moving) grid** — such as a ship or rover — the plugin automatically sets its background alpha to `0` (fully transparent). The CCTV feed becomes a HUD overlay visible through the cockpit windshield without blocking the pilot's view.

`LcdBackgroundAlpha` in the instance config controls the background opacity for **static-grid** LCDs only (0 = transparent, 255 = fully opaque black, default). Dynamic-grid LCDs always use `0` regardless of this setting.

**Recommended vehicle HUD setup:**
1. Place a **Transparent LCD** panel over the cockpit viewport
2. Assign it as the `LiveFeedLcdName` for the instance
3. Set `UseColorMode=false` and `LcdFontTint` to a green tint (e.g. `0,200,80`) for a night-vision overlay effect
4. The background turns transparent automatically — no extra config required

---

## How It Works

1. The Torch plugin scans for camera blocks whose names start with the configured `CameraPrefix` (default `LCD_TVCamera`)
2. On each camera cycle it sends a multiplayer message containing the camera's world position and orientation
3. The client-side mod receives the message and calls `SetCameraController()` locally — no character moves, no physics involved
4. `CCTVCapture.exe` captures the SE window, converts it to SE color characters (0xE100 palette, 512 colors), GZip-compresses the frame and sends it over TCP
5. The plugin decompresses the frame on a background thread, queues it, then writes it to the matching LCDs on the game thread

### Frame routing

```
LCD_TVCamera Bridge  →  LCD_TV Bridge  (or LCD_TV Bridge_TL/TR/BL/BR for 2×2)
LCD_TVCamera Hangar  →  LCD_TV Hangar
```

---

## Configuration

`Torch/Instance/CCTVPlugin.cfg`:

```xml
<TcpPort>12345</TcpPort>
<CameraRescanTicks>1800</CameraRescanTicks>
<EnableAutoCameraCycling>true</EnableAutoCameraCycling>
<CameraCycleIntervalSeconds>10</CameraCycleIntervalSeconds>
<SpectatorSteamId>YOUR_FAKE_CLIENT_STEAM_ID</SpectatorSteamId>
<LcdPrefix>LCD_TV</LcdPrefix>
<CameraPrefix>LCD_TVCamera</CameraPrefix>
<LcdGridResolution>362</LcdGridResolution>
<CaptureFps>2</CaptureFps>
<UseColorMode>true</UseColorMode>
<DesaturateColorMode>false</DesaturateColorMode>
<GridFontSize>0.1</GridFontSize>
<GridVerticalOffset>0</GridVerticalOffset>
<SingleLcdFontSize>0.080</SingleLcdFontSize>
<ProximityCheckRadius>150</ProximityCheckRadius>
<UseMultiClientMode>false</UseMultiClientMode>
```

`LcdGridResolution` — output resolution for the 2×2 LCD grid (and capture). Must be an even number between 64 and 700. The single-LCD resolution is always half this value. **Auto-calculated from `GridFontSize`** so the content exactly fills each panel at the chosen font — move the font slider and the resolution follows. Can still be overridden manually.

`GridFontSize` — base font size for 2×2 grid panels (0.05–0.15). Changing this value auto-calculates `LcdGridResolution` to fill the panel edge-to-edge (e.g. 0.055 → 658, 0.075 → 482, 0.100 → 362). Grayscale automatically doubles this value.

`DesaturateColorMode` — when `true` (and `UseColorMode` is also `true`), the captured image is desaturated to grayscale before encoding into SE color characters. This produces **square-pixel B&W** output using the color char pipeline — no 1:2 aspect ratio issues, auto-fit resolution works correctly, and dithering still applies. The classic grayscale mode (`UseColorMode=false`) is kept for LCD font tint support.

`GridVerticalOffset` — row offset for the 2×2 grid to close the physical seam between top and bottom LCD panels (−30 to +30, default 5). With auto-fit resolution the offset should be 0 or very small.

`SingleLcdFontSize` — base font size for single LCD panels (0.05–0.15). Independent of grid font size.

`ProximityCheckRadius` — distance in metres within which at least one player must be present for LCD writes to occur. Set to `0` to always write regardless of player position.

### Multi-client mode

To run multiple fake clients simultaneously (one per faction, area, etc.) set `UseMultiClientMode` to `true` and define instances:

```xml
<UseMultiClientMode>true</UseMultiClientMode>
<ClientInstances>
  <Instance>
    <Name>Client1</Name>
    <TcpPort>12345</TcpPort>
    <CameraPrefix>LCD_TVCamera</CameraPrefix>
    <CameraSuffix>Test01</CameraSuffix>
    <LcdPrefix>LCD_TV</LcdPrefix>
    <LiveFeedLcdName>Test01</LiveFeedLcdName>
    <LcdBackgroundAlpha>255</LcdBackgroundAlpha>
    <SpectatorSteamId>111111111111111</SpectatorSteamId>
    <Enabled>true</Enabled>
  </Instance>
  <Instance>
    <Name>Client2</Name>
    <TcpPort>12346</TcpPort>
    <CameraPrefix>LCD_TVCamera</CameraPrefix>
    <CameraSuffix>Test02</CameraSuffix>
    <LcdPrefix>LCD_TV</LcdPrefix>
    <LiveFeedLcdName>Test02</LiveFeedLcdName>
    <LcdBackgroundAlpha>255</LcdBackgroundAlpha>
    <SpectatorSteamId>222222222222222</SpectatorSteamId>
    <Enabled>true</Enabled>
  </Instance>
</ClientInstances>
```

Each instance requires its own running `CCTVCapture.exe` connecting on the matching port.

---

## Features

- True color video — SE's hidden 0xE100 palette (512 colors, 9-bit RGB)
- **Desaturate (B&W) mode** — square-pixel grayscale via the color char pipeline; no aspect ratio issues, dithering still applies
- **Auto-fit resolution** — `GridFontSize` automatically calculates `LcdGridResolution` so content fills each panel edge-to-edge with no seam
- GZip frame compression (~14× ratio over uncompressed; negligible bandwidth)
- Configurable LCD render resolution — single slider controls capture and grid resolution (single LCD = half)
- 2×2 grid offset sliders — close the physical seam between LCD panels
- Independent font size tuning — separate Grid Font Size and Single LCD Font Size controls
- Slave LCD support — single slaves (`LCD_TV Test01_Slave`) and grid quadrant slaves (`LCD_TV Test01_TL_Slave`); any number per master; slave grids require an active antenna
- Multi-client mode — independent camera sets per instance
- **Button panel control** — Next / Prev / Reset actions assignable to any in-game button panel via G-menu
- **Camera loops** — group cameras into `_L1`/`_L2` sets; Next Loop / Prev Loop switches the active group on the same LCD with no stale frame
- **Auto HUD mode** — LCDs on moving (non-static) grids automatically receive a fully transparent background, turning the feed into a cockpit HUD overlay
- Pre-emptive teleport — GOTO sent ahead of the display switch to hide latency
- Adaptive cycle timing — EWMA of settle times, floored at the configured interval; resets automatically on every loop switch so the new loop's cameras re-tune independently
- Proximity gate — LCD writes pause automatically when no players are nearby
- LCD reference caching — entity scans only on startup and rescan, not per frame

---

## Troubleshooting

**No cameras found**
Check camera names start with the configured `CameraPrefix`. A rescan runs every `CameraRescanTicks` ticks. Check Torch logs for `Updated camera list`.

**Slave LCDs not updating**
The slave LCD's grid must have a powered, broadcasting radio antenna. Without one the grid is excluded at rescan time. Enable an antenna on the grid and wait for the next rescan (`CameraRescanTicks` ticks).

**LCDs stop updating after switching to a new loop**
The settle-time EWMA now resets on every loop switch. If you were on an older build, update to the latest release — the fix ensures the new loop's cycle interval starts at the 3-second conservative default and re-tunes itself rather than inheriting a potentially very long value from the previous loop.

**LCDs stop updating shortly after enabling grayscale dithering**
This was caused by an `IndexOutOfRangeException` in the dithering path on any frame with medium-to-bright content. Fixed in the latest build — update `CCTVCapture.exe`.

**LCDs not updating**
Verify the LCD custom name matches the pattern exactly: `{LcdPrefix} {camera base name}`. Names are case-insensitive.

**Button panel actions not appearing in G-menu**
Ensure the client-side mod (`CCTVMod`) is enabled in world settings. Actions register on the first game tick after session load — if you open the G-menu immediately on join, wait a moment and reopen it.

**Button panel press does nothing**
Check the button panel's **Custom Data** contains exactly the `LiveFeedLcdName` value from the plugin config (e.g. `Test01`). No spaces, no quotes. Also confirm the Torch log shows `🎮 CAMCTRL received:` when the button is pressed — if that line is absent the message never reached the plugin.

**Teleportation not working**
Set `SpectatorSteamId` to the Steam ID of the fake client account. Ensure the client-side mod is enabled and the fake client is in spectator mode (F8) before connecting.

**CCTVCapture.exe not connecting**
Confirm the port matches the plugin config and no firewall is blocking it. For multi-client setups pass `--port XXXX` to each `CCTVCapture.exe` instance.

---

## Changelog

### v1.4.0
- **Auto-fit grid resolution:** `GridFontSize` now automatically calculates `LcdGridResolution` so the rendered content exactly fills each LCD panel at the chosen font size. At font 0.055 the grid is 658×658 (329 chars per panel), at 0.100 it's 362×362 (181 per panel). No manual offset needed — simple 4-way equal split.
- **Desaturate (B&W) mode:** New `DesaturateColorMode` option strips colour from the captured image before encoding into SE color characters, producing square-pixel grayscale output. Uses the same color char pipeline as full colour — no 1:2 aspect ratio issues, auto-fit resolution works correctly, and dithering still applies. The classic grayscale mode (`UseColorMode=false`) is kept for LCD font tint support.
- **Resolution cap raised to 700:** Both the plugin and CCTVCapture now accept grid resolutions up to 700 (was 484/512), supporting smaller font sizes that need more characters per panel.
- **Grid offset rework:** Reverted the padding-based offset approach to the original row-skipping method. With auto-fit resolution the offsets should be 0 or very small (only needed for the physical gap between LCD blocks).

### v1.3.0
- **Unified resolution control:** `LcdGridResolution` is now the single source of truth — changing it automatically syncs `CaptureWidth` and `CaptureHeight`. The separate Resolution Width/Height fields have been removed from the UI.
- **Grid Vertical Offset:** New setting (`GridVerticalOffset`, 0–10 rows) adds extra rows to the top grid panels (TL/TR), pushing their content further down the LCD and closing the vertical seam between top and bottom panels. Adjustable via slider in the Torch UI.
- **Independent font size controls:** `GridFontSize` and `SingleLcdFontSize` are now separately configurable via sliders, allowing the 2×2 grid and single LCD to be tuned independently.

### Previous
- **Fix — loop switch cameras stopping after a while:** When switching loops (e.g. L1 → L2) the settle-time EWMA and observation counter are now reset so the new loop's auto-cycle interval adapts fresh from its 3-second conservative default instead of inheriting a potentially extended value tuned for the previous loop's camera positions.
- **Fix — missed teleport after rescan during loop cycling:** Stale pre-teleport state (`_preTeleportSent` / `_nextCameraIndexForPreTP`) is now cleared whenever a periodic rescan rebuilds the camera list. Previously, if a rescan fired between a pre-emptive GOTO and the actual display switch, the cycle could incorrectly treat the TP as already sent and skip it — leaving the spectator at the wrong camera.
- **Fix — grayscale dithering crash on bright scenes:** `ConvertToAsciiDithered` used `RICH_RAMP.Length - 1` (= 9) to compute and clamp the character index, then indexed into `BLOCK_RAMP` which only has 5 elements. Any frame with a pixel brighter than ~44% grey caused an `IndexOutOfRangeException`, silently killing the async frame task and halting LCD updates. Index arithmetic now uses `BLOCK_RAMP.Length - 1` throughout.
- **Color dithering strength restored to 1.0:** `DITHER_STRENGTH` in `ConvertToColorCharsDithered` is `1.0f` (full Floyd-Steinberg error propagation) for best overall image quality on SE's 8-level palette. The previous 0.75 reduction suppressed rainbow fringing on hard colour edges at the cost of visible banding; full strength is the better trade-off.

---

## Credits

Inspired by **[Whip's Image Converter](https://steamcommunity.com/sharedfiles/filedetails/?id=323396946)** by Whiplash141. Whip's work on converting images to SE LCD character art — and in particular his research into the 0xE100 hidden color palette — provided both the inspiration for this system and the foundation for achieving the color quality it has.

---
