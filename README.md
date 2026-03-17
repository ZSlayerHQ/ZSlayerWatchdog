<div align="center">

# ZSlayer Watchdog

**Desktop process manager for SPT server + FIKA headless clients**

[![License: MIT](https://img.shields.io/badge/License-MIT-c8aa6e.svg)](LICENSE)
[![SPT](https://img.shields.io/badge/SPT-4.0.x-c8aa6e.svg)]()
[![FIKA](https://img.shields.io/badge/FIKA-Compatible-4a7c59.svg)]()
[![Version](https://img.shields.io/badge/v2.0.0-c8aa6e.svg)](https://github.com/ZSlayerHQ/ZSlayerWatchdog/releases)
[![.NET](https://img.shields.io/badge/.NET-9.0-512bd4.svg)]()
[![WebView2](https://img.shields.io/badge/WebView2-Desktop-blue.svg)]()

---

A desktop application with a WebView2 HTML frontend that manages your SPT server and FIKA headless client processes. Auto-start, auto-restart on crash, WebSocket communication with the Command Center, token-based authentication, and a custom animated wolf head mascot — all in a dark-themed UI matching the [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter).

[Discord](https://discord.gg/ZSlayerHQ) | [YouTube](https://www.youtube.com/@ZSlayerHQ-ImBenCole)

</div>

---

## Features

### Process Management
- **Auto-Start** — launch the SPT server and headless client automatically on startup with configurable delays
- **Auto-Restart** — detect crashes and restart processes automatically
- **Manual Controls** — Start / Stop / Restart buttons for both server and headless
- **Session Timeout** — configurable session timeout slider (1–30 minutes)
- **Restart After Raids** — auto-restart headless after N raids (0–10, 0 = disabled)
- **Crash Tracking** — restart and crash counters displayed per process
- **Readiness Detection** — HTTP health check for server readiness, log polling for headless readiness with failure state
- **External Process Detection** — detects externally-launched processes and gates headless start on server ready
- **Console Visibility** — toggle server/headless console window visibility (Server Hidden, Headless Hidden)

### Communication & Security
- **WebSocket Client** — real-time WebSocket connection to the Command Center server (replaces old HTTP API)
- **Token Authentication** — auto-discovers auth token from `watchdog-token.txt`, 4001 rejection handling, command whitelist
- **Security Card** — UI panel for token management with auth-aware status bar

### UI & Visuals
- **WebView2 Frontend** — full HTML/CSS/JS UI rendered via WebView2, no XAML
- **Wolf Head Mascot** — low-poly 3D wolf head with idle animations (blinking, sniffing, ear flicks, head tilt, breathing)
- **Boot Sound** — synthesized 4-phase boot sound synced to wolf animation with eye burst effects, mute persisted in config
- **System Tray** — minimize to tray option, dark-themed context menu with quick actions
- **Auto-Detecting Deployment** — remote headless support with automatic deployment mode detection
- **Shutdown Confirm** — dialog confirmation before stopping processes

### General
- **Config Persistence** — all settings saved to the CC mod's `config.json` with debounced writes
- **Update Checker** — checks GitHub for new releases on startup
- **Open Command Center** — one-click button to open the CC web panel in your browser

---

## Installation

Place the `ZSlayer Watchdog` folder next to your SPT directory:

```
SPT 2026 Headless/
├── ZSlayer Watchdog/
│   ├── ZSlayerWatchdog.exe
│   ├── ZSlayerWatchdog.dll
│   ├── ZSlayerWatchdog.deps.json
│   ├── ZSlayerWatchdog.runtimeconfig.json
│   ├── watchdog-ui.html
│   ├── app.ico
│   └── runtimes/
└── SPT/
    ├── SPT.Server.exe
    └── user/
        └── mods/
            └── ZSlayerCommandCenter/
                └── config/
                    └── config.json
```

Run `ZSlayerWatchdog.exe`. It automatically discovers `SPT.Server.exe` and reads its configuration from the Command Center config file.

---

## UI Layout

The UI is a WebView2-rendered HTML frontend featuring a low-poly wolf head mascot flanked by server and headless status cards, with process controls, sliders, and toggle switches in a dark-themed layout matching the Command Center.

---

## Configuration

The watchdog reads and writes to the Command Center's `config.json`. All settings are editable from the UI — no manual config editing needed.

### Server Settings

| Setting | Default | Description |
|:--------|:-------:|:------------|
| Auto-Restart | On | Restart server automatically on crash |
| Auto-Start | On | Start server when watchdog launches |
| Auto-Start Delay | 3s | Seconds to wait before auto-starting server |
| Session Timeout | 5 min | Session timeout value (1–30 minutes) |

### Headless Settings

| Setting | Default | Description |
|:--------|:-------:|:------------|
| Auto-Restart | On | Restart headless automatically on crash |
| Auto-Start | Off | Start headless when server is running |
| Auto-Start Delay | 30s | Seconds to wait after server starts |
| Restart After Raids | Off | Restart headless after N raids (0 = disabled) |

### Watchdog Settings

| Setting | Default | Description |
|:--------|:-------:|:------------|
| Minimize to Tray | Off | Close/minimize hides to system tray instead of quitting |
| API Port | 6971 | HTTP API port for remote control |

---

## System Tray

When **Minimize to Tray** is enabled:
- Clicking **✕** or **—** hides the window to the system tray
- Double-click the tray icon to restore
- Right-click for a context menu: Show Window, Open CC, Restart Server, Restart Headless, Quit

When disabled:
- **✕** quits the application (stops all managed processes)
- **—** minimizes to taskbar normally

---

## Communication

The watchdog connects to the Command Center server via WebSocket for real-time bidirectional communication. Authentication uses a shared token (`watchdog-token.txt`) with automatic discovery.

| Feature | Description |
|:--------|:------------|
| **WebSocket** | Persistent connection to CC server for status updates and remote commands |
| **Token Auth** | Shared secret token with 4001 rejection handling |
| **Command Whitelist** | Only authorized commands accepted from the server |
| **Auto-Reconnect** | Automatic reconnection on connection loss |

---

## Requirements

| Requirement | Version |
|:------------|:--------|
| **SPT** | 4.0.x |
| **.NET Runtime** | 9.0 (desktop runtime) |
| **ZSlayer Command Center** | 2.12+ (for config file + WebSocket) |
| **OS** | Windows 10/11 |

---

## Related Projects

| Project | Description |
|:--------|:------------|
| [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter) | Browser-based admin panel for SPT/FIKA |
| [ZSlayer Headless Telemetry](https://github.com/ZSlayerHQ/ZSlayerHeadlessTelemetry) | Live raid telemetry BepInEx plugin |

---

## License

[MIT](LICENSE) — Built by [ZSlayerHQ / Ben Cole](https://github.com/ZSlayerHQ)
