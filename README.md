# Skua Linux

Unofficial Linux port of **Skua**, using the original Skua.Core architecture with an Electron/Ruffle game client and a native .NET backend.

> Current Linux release candidate: **1.2.0** · first validated target: **Linux x86_64**.

## Architecture

```text
C# Scripts
   ↓
Skua.Core / ScriptInterface
   ↓
IFlashUtil
   ↓
RuffleFlashUtil
   ↓
RuffleBridgeClient
   ↓
WebSocket :8182
   ↓
Electron main.js broker
   ↓
Renderer JavaScript
   ↓
Ruffle ExternalInterface
   ↓
Skua AS3 / AQW
```

The Linux frontend does not implement a second bot engine. Automation logic remains in Skua.Core; the Linux-specific layers adapt the original interfaces to Ruffle/Electron.

## Linux-specific components

- `Skua.Backend.Linux/` — .NET backend, Ruffle bridge and Linux platform services.
- `Skua.Linux.Client/` — Electron/Ruffle frontend and AppImage packaging.
- Existing `Skua.Core*` and `Skua.AS3` projects remain the core of the application.

## Current functionality

The current release candidate includes script compilation/execution, CoreBots compatibility, game options, application themes, game backgrounds, runtime helpers, fast travel, drops, loader/grabber/junk/stats/console tools, advanced skills, packet tools, bank access, logs, auto combat controls, backend recovery and Ruffle cache/reload controls.

Two recovery controls are available directly in the application chrome:

- **Restart Backend** — restarts the Electron-owned .NET backend without restarting the whole application.
- **Reload Ruffle** — clears client HTTP/CacheStorage state and recreates the AQW/Ruffle renderer while keeping the Electron process alive.

## Development

Build the backend first:

```fish
dotnet build Skua.Backend.Linux/Skua.Backend.Linux.csproj
```

Run the Electron client:

```fish
cd Skua.Linux.Client
npm install
npm start
```

The development launcher automatically discovers the backend from the monorepo layout.

## Build AppImage

Requirements include the .NET 10 SDK, Node/npm, fish and the normal Electron Builder Linux packaging dependencies.

```fish
cd Skua.Linux.Client
fish build-appimage.fish
```

Generated release artifacts are written under `artifacts/` at the repository root and build logs under `build-logs/`. These directories are ignored by Git.

The AppImage bundles a self-contained untrimmed .NET backend because Skua uses Roslyn scripting, reflection and dynamically loaded assemblies.

## Runtime notes

- The packaged application stores writable Skua runtime state under the user's configuration directory rather than inside the read-only AppImage mount.
- Ruffle is pinned as a local production dependency rather than downloaded at runtime.
- Native Wayland/NVIDIA acceleration has been validated during development. Other GPU/desktop combinations still need broader community testing.

## Upstream and credits

This repository is a Linux-focused fork of **auqw/Skua** and retains the upstream Git history and contributors.

The Electron client originated from **Aquastar** and its MIT license is preserved in `Skua.Linux.Client/LICENSE`.

Flash emulation is provided by **Ruffle**, distributed separately as an npm dependency under its MIT/Apache-2.0 dual-license terms.

See `THIRD_PARTY_NOTICES.md` for attribution and licensing notes.

## Disclaimer

Skua Linux is a third-party project and is not affiliated with or endorsed by Artix Entertainment or AdventureQuest Worlds.

## Repository

GitHub: https://github.com/Baianic/Skua-Linux
