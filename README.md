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
Install to: `%AppData%\SpaceEngineers\Mods\`

---

## Quick Start

### 1. Install the Torch plugin
Copy `CCTVPlugin.dll` to `Torch/Plugins/` and restart Torch.

### 2. Install the client-side mod
Copy the `CCTVMod` folder to `%AppData%\SpaceEngineers\Mods\` and enable it in world settings. The mod must be active on the fake client's SE instance.

### 3. Name your cameras and LCDs
Camera and LCD names follow a prefix + base-name pattern:

| Camera name | Single LCD | 2×2 Grid LCDs |
|---|---|---|
| `LCD_TVCamera Test01` | `LCD_TV Test01` | `LCD_TV Test01_TL/TR/BL/BR` |
| `LCD_TVCamera Hangar` | `LCD_TV Hangar` | `LCD_TV Hangar_TL/TR/BL/BR` |

Slave LCDs (copies of a master) can be named `LCD_TV Test01_TL_Slave`, `LCD_TV Test01_TL_Slave2`, etc.

> **Antenna required for slave LCDs:** A slave LCD's grid must have at least one active, broadcasting radio antenna. Grids without a powered antenna are skipped automatically at rescan time.

### 4. Run the fake client
1. Launch SE on the fake account in **windowed mode**
2. Connect to the server and press **F8** (spectator mode)
3. Run `CCTVCapture.exe`

The plugin will detect cameras, send GOTO messages and begin cycling. LCDs update automatically.

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
<CaptureWidth>181</CaptureWidth>
<CaptureHeight>181</CaptureHeight>
<CaptureFps>2</CaptureFps>
<UseColorMode>true</UseColorMode>
<ProximityCheckRadius>150</ProximityCheckRadius>
<UseMultiClientMode>false</UseMultiClientMode>
```

`ProximityCheckRadius` — distance in metres within which at least one player must be present for LCD writes to occur. Set to `0` to always write regardless of player position.

### Multi-client mode

To run multiple fake clients simultaneously (one per faction, area, etc.) set `UseMultiClientMode` to `true` and define instances:

```xml
<UseMultiClientMode>true</UseMultiClientMode>
<ClientInstances>
  <Instance>
    <Name>Alpha</Name>
    <TcpPort>12345</TcpPort>
    <CameraPrefix>LCD_TVCamera_A</CameraPrefix>
    <LcdPrefix>LCD_TV</LcdPrefix>
    <SpectatorSteamId>111111111111111</SpectatorSteamId>
    <Enabled>true</Enabled>
  </Instance>
  <Instance>
    <Name>Bravo</Name>
    <TcpPort>12346</TcpPort>
    <CameraPrefix>LCD_TVCamera_B</CameraPrefix>
    <LcdPrefix>LCD_TV</LcdPrefix>
    <SpectatorSteamId>222222222222222</SpectatorSteamId>
    <Enabled>true</Enabled>
  </Instance>
</ClientInstances>
```

Each instance requires its own running `CCTVCapture.exe` connecting on the matching port.

---

## Features

- True color video — SE's hidden 0xE100 palette (512 colors, 9-bit RGB)
- GZip frame compression (~14× ratio over uncompressed; negligible bandwidth)
- 181×181 single LCD and 362×362 2×2 grid modes
- Slave LCD support — any number of copies per quadrant panel; slave grids require an active antenna
- Multi-client mode — independent camera sets per instance
- Pre-emptive teleport — GOTO sent ahead of the display switch to hide latency
- Adaptive cycle timing — EWMA of settle times, floored at the configured interval
- Proximity gate — LCD writes pause automatically when no players are nearby
- LCD reference caching — entity scans only on startup and rescan, not per frame

---

## Troubleshooting

**No cameras found**
Check camera names start with the configured `CameraPrefix`. A rescan runs every `CameraRescanTicks` ticks. Check Torch logs for `Updated camera list`.

**Slave LCDs not updating**
The slave LCD's grid must have a powered, broadcasting radio antenna. Without one the grid is excluded at rescan time. Enable an antenna on the grid and wait for the next rescan (`CameraRescanTicks` ticks).

**LCDs not updating**
Verify the LCD custom name matches the pattern exactly: `{LcdPrefix} {camera base name}`. Names are case-insensitive.

**Teleportation not working**
Set `SpectatorSteamId` to the Steam ID of the fake client account. Ensure the client-side mod is enabled and the fake client is in spectator mode (F8) before connecting.

**CCTVCapture.exe not connecting**
Confirm the port matches the plugin config and no firewall is blocking it. For multi-client setups pass `--port XXXX` to each `CCTVCapture.exe` instance.

---

## Credits

Inspired by **[Whip's Image Converter](https://steamcommunity.com/sharedfiles/filedetails/?id=323396946)** by Whiplash141. Whip's work on converting images to SE LCD character art — and in particular his research into the 0xE100 hidden color palette — provided both the inspiration for this system and the foundation for achieving the color quality it has.

---
