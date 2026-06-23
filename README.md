# vibranceGUI2

Per-app color profiles for any game or application on Windows.

When you switch to a different app, vibranceGUI2 automatically applies that app's brightness, contrast, gamma, and vibrance settings to your monitor — and restores defaults when you switch away.

## How it works

- **All GPUs**: brightness, contrast, gamma via `SetDeviceGammaRamp` (built into Windows)
- **NVIDIA**: digital vibrance via NVAPI
- Detects foreground window changes with `SetWinEventHook`

## Features

- Desktop default profile with 4 sliders (Vibrance, Brightness, Contrast, Gamma)
- Per-application profiles with custom color settings
- Tray icon with minimize-to-tray
- Dark, Light, and System theme support
- Auto-start with Windows option
- Reset-to-defaults button

## Requirements

- Windows 10/11
- .NET 8.0 Desktop Runtime

## Build

```
dotnet build
```

## Run

```
dotnet run
```

Settings are saved to `%APPDATA%\vibranceGUI2\settings.json`.
