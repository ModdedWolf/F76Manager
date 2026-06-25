# Fallout 76 Manager


Point it at your game folder (Steam or Xbox/Game Pass), tweak your settings, install mods, and deploy when you're ready. The UI is a web front-end baked into the app with WebView2.

## What it does

### Game tweaks

Change common `Fallout76Custom.ini` / `Fallout76Prefs.ini` settings without opening a text editor every time:

- Graphics and performance (godrays, grass, shadows, LOD, water, decals, and more)
- Frame rate cap and VSync
- FOV (world and Pip-Boy)
- Motion blur, depth of field, lens flare, VATS blur
- Faster loading options
- Network / bandwidth tweaks

There's also a built-in INI editor if you want to go off-script.

### Mod manager

- Install mods from archives, folders, or `.ba2` files — drag and drop
- Enable/disable mods, set load order, and deploy to your game
- Bundle mods into shared `.ba2` archives or deploy loose files
- Conflict detection so you know when two mods step on each other
- **Profiles** — save snapshots of mods, load order, and tweaks and switch between them (e.g. "Ultra Graphics" vs "Performance")
- **Virtual Mod Mode** — stage mods in a manager folder instead of copying straight into `Data` (handy for protected installs)
- Mod groups, backups, and a dashboard that shows config health at a glance

### Pip-Boy & themes

- Pip-Boy color and screen customization with a preview
- Several bundled UI themes, plus import your own `.f76theme` packages

## Requirements

- Windows 10/11 (x64)
- WebView2 Runtime (usually already on your system via Edge)
- For building from source: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

Optional: **7-Zip** or **WinRAR/UnRAR** for `.7z` / `.rar` mod archives (auto-detected; configurable in Settings).

## Build from source

```powershell
cd AppWrapper\F76ManagerApp
.\build.ps1
```

That syncs `WebSrc/`, publishes `F76Manager` (`win-x64`, self-contained), and puts release zips under `Release/`.

## Project layout

```
AppWrapper/F76ManagerApp/   Main app (F76Manager.exe)
  Managers/                 Mod, bundle, and config logic
  Form1.*.cs                WinForms host + WebView2 bridge
  build.ps1                 Build script
WebSrc/                     UI (embedded at build time)
```

Source is on GitHub: https://github.com/ModdedWolf/F76Manager-Source
