<div align="center">

# ZSlayer Watchdog

**Desktop process manager for SPT server + FIKA headless clients**

[![License: MIT](https://img.shields.io/badge/License-MIT-c8aa6e.svg)](LICENSE)
[![SPT](https://img.shields.io/badge/SPT-4.0.x-c8aa6e.svg)]()
[![FIKA](https://img.shields.io/badge/FIKA-Compatible-4a7c59.svg)]()
[![.NET](https://img.shields.io/badge/.NET-9.0-512bd4.svg)]()
[![WPF](https://img.shields.io/badge/WPF-Desktop-blue.svg)]()

---

A lightweight WPF desktop application that manages your SPT server and FIKA headless client processes. Auto-start, auto-restart on crash, system tray support, and a built-in HTTP API — all wrapped in a dark-themed UI that matches the [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter).

[Discord](https://discord.gg/ZSlayerHQ) | [YouTube](https://www.youtube.com/@ZSlayerHQ-ImBenCole)

</div>

---

## Features

- **Auto-Start** — launch the SPT server and headless client automatically on startup with configurable delays
- **Auto-Restart** — detect crashes and restart processes automatically
- **Manual Controls** — Start / Stop / Restart buttons for both server and headless
- **Session Timeout** — configurable session timeout slider (1–30 minutes)
- **Restart After Raids** — auto-restart headless after N raids (0–10, 0 = disabled)
- **Crash Tracking** — crash counter displayed per process
- **System Tray** — minimize to tray option, dark-themed context menu with quick actions
- **Toggle Switches** — all boolean settings are interactive toggle switches matching the CC web UI style
- **Config Persistence** — all settings saved to the CC mod's `config.json` with debounced writes
- **Update Checker** — checks GitHub for new releases on startup
- **HTTP API** — built-in REST API for remote status queries and control
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
│   └── app.ico
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

```
┌──────────────────────────────────────────────────────┐
│ ZSLAYER — WATCHDOG                    v2.4.0   — ✕   │
├──────────────────────────┬───────────────────────────┤
│ 🖥 SPT SERVER    ● Run  │ 🖥 HEADLESS      ● Run   │
│                          │                           │
│ UPTIME  PID  AUTO-RST   │ UPTIME  PID    AUTO-RST   │
│ 5h42m  3568  [===]      │ 5h41m  2593   [===]       │
│              AUTO-START  │                           │
│              [===]       │ AUTO-START  DELAY  PROFILE│
│                          │ [===]       30s    🤖     │
│ SESSION TIMEOUT   5 min  │                           │
│ ═══●═════════════════    │ RESTART AFTER RAIDS    3  │
│                          │ ═══●══════════════════    │
│ CRASHES TODAY            │                           │
│ 0                        │ CRASHES TODAY             │
│                          │ 0                         │
│    [Start][Stop][Rstrt]  │    [Start][Stop][Rstrt]   │
├──────────────────────────┴───────────────────────────┤
│ API: http://127.0.0.1:6971 │ MIN→TRAY [=]           │
│              [Open Command Center]            [Quit] │
└──────────────────────────────────────────────────────┘
```

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

## HTTP API

The watchdog exposes a REST API on the configured port (default `6971`).

| Method | Endpoint | Description |
|:-------|:---------|:------------|
| `GET` | `/watchdog/status` | Server + headless status, PIDs, uptime |
| `POST` | `/watchdog/server/start` | Start the SPT server |
| `POST` | `/watchdog/server/stop` | Stop the SPT server |
| `POST` | `/watchdog/server/restart` | Restart the SPT server |
| `POST` | `/watchdog/headless/start` | Start the headless client |
| `POST` | `/watchdog/headless/stop` | Stop the headless client |
| `POST` | `/watchdog/headless/restart` | Restart the headless client |

---

## Requirements

| Requirement | Version |
|:------------|:--------|
| **SPT** | 4.0.x |
| **.NET Runtime** | 9.0 (desktop runtime) |
| **ZSlayer Command Center** | 2.3+ (for config file) |
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
