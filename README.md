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
External application. Captures the SE window, converts frames to color ASCII and streams them to the plugin over TCP. Shows a **settings GUI** on launch for connection, visual mode, quality and alignment options.
Run from: `CCTVCapture/bin/Release/net48/CCTVCapture.exe`

Command-line arguments (override saved settings):
```
CCTVCapture.exe --port 12346 --host 192.168.1.100 --nogui --verbose
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

> **⚠️ Capture client placement:** Spawn or park the fake client's character **away from your CCTV LCDs** (at least outside `ProximityCheckRadius`, default 150 m). The proximity gate pauses LCD writes when no *main account* player is nearby — if the capture client's character is standing next to the displays it counts as a nearby player and the LCDs will never sleep, wasting server resources when nobody is actually watching.

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
4. `CCTVCapture.exe` captures the SE window, converts it to SE color characters (0xE100 palette, 512 colors), GZip-compresses the result and sends it over TCP using a binary framing protocol (8-byte header: `[4B type][4B length]` + raw payload). For 2×2 grids the image is split into quadrants inside CCTVCapture before sending — each quadrant travels as its own `MSG_QUAD` frame. The connection opens with an HMAC-SHA256 challenge/response handshake
5. The plugin decompresses each frame on a background thread, queues it, then writes it to the matching LCDs on the game thread

### Frame routing

```
LCD_TVCamera Bridge  →  LCD_TV Bridge  (or LCD_TV Bridge_TL/TR/BL/BR for 2×2)
LCD_TVCamera Hangar  →  LCD_TV Hangar
```

---

## Configuration

### Server — `Torch/Instance/CCTVPlugin.cfg`

```xml
<TcpPort>12345</TcpPort>
<CameraRescanTicks>1800</CameraRescanTicks>
<EnableHeartbeat>false</EnableHeartbeat>
<EnableAutoCameraCycling>true</EnableAutoCameraCycling>
<CameraCycleIntervalSeconds>10</CameraCycleIntervalSeconds>
<SpectatorSteamId>YOUR_FAKE_CLIENT_STEAM_ID</SpectatorSteamId>
<LcdPrefix>LCD_TV</LcdPrefix>
<CameraPrefix>LCD_TVCamera</CameraPrefix>
<LcdFontTint>255,255,255</LcdFontTint>
<LcdGridResolution>362</LcdGridResolution>
<GrayscaleGridResolution>362</GrayscaleGridResolution>
<CaptureFps>10</CaptureFps>
<DisplayFps>10</DisplayFps>
<UseColorMode>true</UseColorMode>
<DesaturateColorMode>false</DesaturateColorMode>
<NightVisionMode>false</NightVisionMode>
<CropCaptureToSquare>true</CropCaptureToSquare>
<HorizontalSquash>1.0</HorizontalSquash>
<SingleHorizontalSquash>1.0</SingleHorizontalSquash>
<DitherMode>None</DitherMode>
<PostProcessMode>None</PostProcessMode>
<GridPostProcessMode>None</GridPostProcessMode>
<GridFontSize>0.1</GridFontSize>
<GridContentShift>0</GridContentShift>
<GridVerticalOffset>5</GridVerticalOffset>
<GridHorizontalOffset>0</GridHorizontalOffset>
<SingleLcdFontSize>0.1</SingleLcdFontSize>
<SingleContentShift>0</SingleContentShift>
<FontScale>1.0</FontScale>
<AutoAdjustFontSize>true</AutoAdjustFontSize>
<ProximityCheckRadius>150</ProximityCheckRadius>
<EnableVerboseFrameLogging>false</EnableVerboseFrameLogging>
<UseMultiClientMode>false</UseMultiClientMode>
```

#### Setting reference

| Setting | Default | Description |
|---|---|---|
| `TcpPort` | 12345 | TCP port for CCTVCapture connections (legacy single-client mode) |
| `CameraRescanTicks` | 1800 | Ticks between camera block rescans (60 ticks = 1 second) |
| `EnableHeartbeat` | false | Enable PING/PONG heartbeat between plugin and capture client |
| `EnableAutoCameraCycling` | true | Automatically cycle through cameras |
| `CameraCycleIntervalSeconds` | 10 | Seconds between camera switches (min 5) |
| `SpectatorSteamId` | 0 | Steam ID of the fake client account |
| `CameraPrefix` | LCD_TVCamera | Prefix for camera block names |
| `LcdPrefix` | LCD_TV | Prefix for LCD panel names |
| `LcdFontTint` | 255,255,255 | RGB tint for grayscale LCD font color |
| `LcdGridResolution` | 362 | Color mode resolution for the 2×2 grid (even, 64–700). Auto-calculated from `GridFontSize` |
| `GrayscaleGridResolution` | 362 | Grayscale mode resolution for the 2×2 grid (even, 64–700). Independent of color resolution — grayscale can run higher without affecting color bandwidth |
| `CaptureFps` | 10 | Capture FPS (1–30). Server maximum — clients clamp to this |
| `DisplayFps` | 10 | LCD write FPS (1–10). Must be ≤ CaptureFps |
| `UseColorMode` | true | Enable 512-color SE palette mode |
| `DesaturateColorMode` | false | Square-pixel B&W via color chars (requires Color Mode) |
| `NightVisionMode` | false | Green night-vision phosphor tint (requires Desaturate) |
| `CropCaptureToSquare` | true | Center-crop 16:9 viewport to 1:1 for correct proportions |
| `HorizontalSquash` | 1.0 | Horizontal aspect correction for 2×2 grid (0.5–1.5). >1.0 compresses horizontally |
| `SingleHorizontalSquash` | 1.0 | Horizontal aspect correction for single LCD (0.5–1.5) |
| `DitherMode` | None | Dithering algorithm: `None`, `Bayer`, `FloydSteinberg` |
| `PostProcessMode` | None | Pre-filter for single LCD: `None`, `LightBlur`, `MediumBlur`, `Sharpen` |
| `GridPostProcessMode` | None | Pre-filter for 2×2 grid: `None`, `LightBlur`, `MediumBlur`, `Sharpen` |
| `GridFontSize` | 0.1 | Base font for 2×2 grid panels (0.05–0.15). Auto-calculates `LcdGridResolution` |
| `GridContentShift` | 0 | Horizontal content shift for grid panels in characters (−100 to +100) |
| `GridVerticalOffset` | 5 | Vertical row offset to close the grid seam (−30 to +30) |
| `GridHorizontalOffset` | 0 | Horizontal column offset to close the grid seam (−30 to +30) |
| `SingleLcdFontSize` | 0.1 | Base font for single LCD panels (0.05–0.15) |
| `SingleContentShift` | 0 | Horizontal content shift for single LCD in characters (−100 to +100) |
| `FontScale` | 1.0 | Global font scale multiplier |
| `AutoAdjustFontSize` | true | Scale font based on resolution automatically |
| `ProximityCheckRadius` | 150 | Distance (m) within which a player must be present for LCD writes. 0 = always write |
| `EnableVerboseFrameLogging` | false | Log `[FRAME]` messages at INFO level |
| `UseMultiClientMode` | false | Enable multiple CCTVCapture instances |

> **Auto-fit resolution:** `GridFontSize` automatically calculates `LcdGridResolution` so content fills each panel edge-to-edge (e.g. 0.055 → 658, 0.075 → 482, 0.100 → 362). Grayscale doubles the font at render time. The `GrayscaleGridResolution` slider is independent so you can run grayscale at higher resolution without increasing color mode bandwidth.

### Client — `CCTVCapture.settings.xml`

CCTVCapture shows a **settings GUI** on launch where you can configure connection, visual mode, quality, and alignment options. Settings are saved to `CCTVCapture.settings.xml` next to the executable and persist between sessions. Pass `--nogui` to skip the form and use saved settings.

Client settings that overlap with server settings (FPS, dithering, post-processing, offsets, squash) are **sent to the server via `CLIENTPREFS`** on connect and applied immediately — no server restart needed. FPS values are clamped to the server maximum.

| Setting | Default | Description |
|---|---|---|
| `Host` | localhost | Server hostname or IP |
| `Port` | 12345 | Server TCP port |
| `UseColorMode` | true | Color or grayscale mode |
| `DesaturateColorMode` | false | B&W via color chars |
| `NightVisionMode` | false | Green NV tint |
| `CropCaptureToSquare` | true | Center-crop to 1:1 |
| `PreferredFps` | 10 | Preferred capture FPS (clamped to server max) |
| `PreferredDisplayFps` | 2 | Preferred LCD write FPS (clamped to server max) |
| `DitherMode` | None | `None` / `Bayer` / `FloydSteinberg` |
| `PostProcessMode` | None | Single LCD pre-filter |
| `GridPostProcessMode` | LightBlur | 2×2 grid pre-filter |
| `HorizontalSquash` | 1.0 | Grid horizontal aspect correction |
| `SingleHorizontalSquash` | 1.0 | Single LCD horizontal aspect correction |
| `GridVerticalOffset` | 5 | Grid vertical seam offset |
| `GridHorizontalOffset` | 0 | Grid horizontal seam offset |
| `GridContentShift` | 0 | Grid horizontal content shift |
| `SingleContentShift` | 0 | Single LCD horizontal content shift |
| `LcdFontTint` | 255,255,255 | Grayscale font tint (R,G,B) |

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
- **Night vision mode** — green phosphor NV tint baked into pixel RGB before encoding. Requires Desaturate mode. Produces a convincing starlight-scope look
- **Crop to Square** — center-crops 16:9 viewport to 1:1 before conversion; correct proportions without needing a custom SE resolution. Toggle off for wider FOV with stretched proportions
- **Horizontal squash correction** — independent aspect-ratio sliders for grid and single LCD to compensate for SE character cell proportions
- **Auto-fit resolution** — `GridFontSize` automatically calculates `LcdGridResolution` so content fills each panel edge-to-edge with no seam
- **Independent grayscale resolution** — `GrayscaleGridResolution` is separate from color resolution, so grayscale can run at higher res without increasing color bandwidth
- **Dithering** — `Bayer` (stable, low flicker) or `FloydSteinberg` (smoother gradients) applied before color quantisation
- **Post-processing filters** — `LightBlur`, `MediumBlur`, or `Sharpen` pre-filters for single LCD and 2×2 grid independently
- **Content shift sliders** — horizontal shift (in characters) for grid and single LCD panels independently to centre the image
- **Binary wire protocol** — post-handshake TCP uses compact 8-byte binary frame headers (`MSG_FRAME` for single LCD, `MSG_QUAD` per grid quadrant, `MSG_TEXT` for control). No text parsing, no partial-read races — significantly improved stream stability
- **Authenticated connection** — HMAC-SHA256 challenge/response handshake (`HELLO` nonce → `AUTH` response). Unauthorized connections are rejected before any frame data is exchanged
- **Stall detection** — 30-second receive timeout on the plugin side. If CCTVCapture stops sending (hung or closed), the plugin logs a warning and cleanly disconnects, immediately readying itself for reconnect
- **TCP KeepAlive** — enabled on the listener socket so silently dropped connections are detected without waiting for the full receive timeout
- GZip frame compression (~14× ratio over uncompressed; negligible bandwidth)
- Configurable LCD render resolution — single slider controls capture and grid resolution (single LCD = half)
- 2×2 grid offset sliders — close the physical seam between LCD panels (vertical and horizontal)
- Independent font size tuning — separate Grid Font Size and Single LCD Font Size controls
- Slave LCD support — single slaves (`LCD_TV Test01_Slave`) and grid quadrant slaves (`LCD_TV Test01_TL_Slave`); any number per master; slave grids require an active antenna
- Multi-client mode — independent camera sets per instance
- **CCTVCapture settings GUI** — graphical settings form on launch with connection, visual mode, quality, and alignment options. Settings persist between sessions. Pass `--nogui` to skip
- **Client preferences sync** — visual settings (dithering, post-processing, offsets, squash, font tint) are sent to the server on connect via `CLIENTPREFS` and applied immediately — no server restart needed
- **Button panel control** — Next / Prev / Reset actions assignable to any in-game button panel via G-menu
- **Camera loops** — group cameras into `_L1`/`_L2` sets; Next Loop / Prev Loop switches the active group on the same LCD with no stale frame
- **Auto HUD mode** — LCDs on moving (non-static) grids automatically receive a fully transparent background, turning the feed into a cockpit HUD overlay
- Pre-emptive teleport — GOTO sent ahead of the display switch to hide latency
- Adaptive cycle timing — EWMA of settle times, floored at the configured interval; resets automatically on every loop switch so the new loop's cameras re-tune independently
- Proximity gate — LCD writes pause automatically when no players are nearby
- LCD reference caching — entity scans only on startup and rescan, not per frame
- **Capture FPS / Display FPS split** — capture at high FPS (up to 30), display at lower FPS (1–10) for smoother buffering with lower LCD write overhead
- **Verbose frame logging** — optional `[FRAME]` messages at INFO level for debugging frame flow

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

**Server hangs / sim-speed drops when running alongside Isy's Inventory Manager**
Isy's Inventory Manager performs heavy grid-wide inventory scans via a Programmable Block script. On servers with many grids these scans can stall the game thread for 50–100ms+, which may cause intermittent freezes that appear to coincide with CCTV camera switches. The CCTV plugin itself is not the cause — its per-tick cost is typically 2–6ms — but the two workloads can collide. If you experience periodic hangs, try disabling Isy's script temporarily to confirm. A fix or workaround is being investigated.

---

## Changelog

### v1.6.0
- **Binary wire protocol:** All post-handshake communication between CCTVCapture and the plugin now uses a compact binary framing format (`[4B message type][4B payload length][N bytes payload]`). `MSG_FRAME` carries single-LCD frames, `MSG_QUAD` carries individual 2×2 grid quadrant frames, and `MSG_TEXT` carries control messages (PING, CONFIG, etc.). Eliminates text-parsing edge cases and partial-read races, significantly improving stream stability.
- **Quadrant splitting moved to CCTVCapture:** The 2×2 grid image is now split into its four quadrants (TL/TR/BL/BR) inside `CCTVCapture.exe` before transmission. Each quadrant travels as its own `MSG_QUAD` binary frame. Removes the split logic from the plugin game thread.
- **HMAC challenge-response handshake:** The TCP handshake now uses an HMAC-SHA256 challenge/response exchange (`HELLO` nonce → `AUTH` response). Unauthorized connections are rejected before any data is exchanged.
- **Stall detection & receive timeout:** The plugin applies a 30-second receive timeout on each client connection. If CCTVCapture stops sending frames (process hung or window closed), the plugin logs a clear warning and cleanly disconnects, immediately readying itself for reconnect.
- **TCP KeepAlive:** Enabled on the listener socket to detect silently dropped connections faster without waiting for the full receive timeout.

### v1.5.0
- **Night Vision mode:** New `NightVisionMode` option (requires Desaturate) maps grayscale luminance to a green phosphor gradient (black → green → white-green) baked directly into pixel RGB. Produces proper NV imagery through the color char pipeline.
- **Independent grayscale resolution:** New `GrayscaleGridResolution` setting allows grayscale mode to run at a different (typically higher) resolution than color mode. Grayscale uses less bandwidth per character, so you can push resolution higher without hitting the same limits.
- **Capture FPS / Display FPS split:** `CaptureFps` and `DisplayFps` are now independent. Capture at up to 30 FPS while displaying at 1–10 FPS for smoother buffering with lower server-side LCD write overhead.
- **Dithering modes:** `DitherMode` replaces the old boolean `UseDithering`. Choose `None`, `Bayer` (stable, low flicker), or `FloydSteinberg` (smoother gradients).
- **Post-processing filters:** `PostProcessMode` and `GridPostProcessMode` apply `LightBlur`, `MediumBlur`, or `Sharpen` pre-filters to single LCD and 2×2 grid independently before character conversion.
- **Horizontal squash correction:** `HorizontalSquash` and `SingleHorizontalSquash` sliders (0.5–1.5) for per-display-type aspect ratio correction. Values >1.0 compress horizontally to compensate for SE character cell proportions.
- **Content shift sliders:** `GridContentShift` and `SingleContentShift` (−100 to +100 chars) move the image horizontally on the LCDs. `GridHorizontalOffset` (−30 to +30 cols) closes the horizontal seam between left/right grid panels.
- **CCTVCapture settings GUI:** Graphical settings form on launch with connection, visual mode, quality, and alignment options. Settings persist to `CCTVCapture.settings.xml`. Pass `--nogui` to skip.
- **Client preferences sync:** Visual settings (dithering, post-processing, offsets, squash, font tint) are pushed to the server via `CLIENTPREFS` on connect and applied immediately — no server restart needed.
- **Verbose frame logging:** New `EnableVerboseFrameLogging` option shows `[FRAME]` messages at INFO level for debugging frame flow.
- **Thread leak fix:** Fixed a critical bug where reconnecting the CCTVCapture client repeatedly would leak send/handler threads on the server, eventually causing a crash. Old threads are now properly stopped and joined before starting new ones.

### v1.4.0
- **Crop to Square:** New `CropCaptureToSquare` option (on by default) center-crops the 16:9 SE viewport to 1:1 before resizing, fixing the ~44% horizontal squash that affected all previous builds. SE can run at any normal resolution — no custom 800×800 resolution needed. Toggle off for wider FOV with stretched proportions.
- **Auto-fit grid resolution:** `GridFontSize` now automatically calculates `LcdGridResolution` so the rendered content exactly fills each LCD panel at the chosen font size. At font 0.055 the grid is 658×658 (329 chars per panel), at 0.100 it's 362×362 (181 per panel). No manual offset needed — simple 4-way equal split.
- **Desaturate (B&W) mode:** New `DesaturateColorMode` option strips colour from the captured image before encoding into SE color characters, producing square-pixel grayscale output. Uses the same color char pipeline as full colour — no 1:2 aspect ratio issues, auto-fit resolution works correctly, and dithering still applies. The classic grayscale mode (`UseColorMode=false`) is kept for LCD font tint support (e.g. green night-vision look).
- **Resolution cap raised to 700:** Both the plugin and CCTVCapture now accept grid resolutions up to 700 (was 484/512), supporting smaller font sizes that need more characters per panel.
- **Performance diagnostics:** `Update()` now tracks per-tick timing with `Stopwatch`. Heartbeat logs include `perf: worst=Xms write=Xms prox=Xms slow=N` and any single tick exceeding 16ms triggers an immediate `⏱️ SLOW TICK` warning in the Torch log.
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

## Further reading

- "Ditherpunk" — Surma's article on creative dithering techniques: https://surma.dev/things/ditherpunk/
- High‑Gain LUT / image enhancement (night‑vision techniques): https://pmc.ncbi.nlm.nih.gov/articles/PMC11507526/

---

## Credits

Inspired by **[Whip's Image Converter](https://steamcommunity.com/sharedfiles/filedetails/?id=323396946)** by Whiplash141. Whip's work on converting images to SE LCD character art — and in particular his research into the 0xE100 hidden color palette — provided both the inspiration for this system and the foundation for achieving the color quality it has.
Tooling - GitHub Copilot — assisted in repository edits and contributor documentation.
---
