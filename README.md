# GroupNB Clock (kiosk)

All commands are PowerShell, run from the repo root:

```powershell
cd C:\Users\JeremiahMonfiel\GroupNB_Edge\GNB-ERP-Clocking
```

## Prerequisites

```powershell
dotnet workload install maui
```

## Settings

Fill in the `.env` file at the repo root before building:

```
ClockKiosk__BaseUrl=
ClockKiosk__ApiKey=
ClockKiosk__TenantIds=
ClockKiosk__OrganizationIds=
ClockKiosk__CameraStallSeconds=5
ClockKiosk__Motion=Auto
ClockKiosk__CameraMaxPhotoAgeMs=1000
ClockKiosk__CameraDailyRebuildAt=03:00
ClockKiosk__LogRetentionDays=14
ClockKiosk__KeepLocalPhotosDays=7
```

`.env` is built into the exe, so rebuild or republish after you change it.

Logs land in `{AppData}/logs/kiosk-yyyyMMdd.log`. Leave `CameraDailyRebuildAt` empty to skip the nightly camera restart.

Each punch photo is a JPEG at most 480 px on the long edge, quality 0.4, and that is the file uploaded. The kiosk deletes its local copies after 7 days (`KeepLocalPhotosDays`). The activity list uses a 96 px thumbnail, not the full photo.

### Old or slow kiosk laptops (e.g. i5 7th gen, 8 GB RAM)

Set these before publishing:

```
ClockKiosk__Motion=Off
ClockKiosk__CameraStallSeconds=15
```

- `Motion=Off` stops the moving background and halo; the screen stays the same, just still. `Auto` already picks `Lite` on 8 GB machines, but Lite still animates.
- `CameraStallSeconds=15` stops a busy CPU from being mistaken for a frozen camera, which forced a costly camera restart.

Every 5 minutes the log writes a `Health` line (memory, handles, threads, camera restarts). If the kiosk keeps closing, send the last `kiosk-*.log` and `%LOCALAPPDATA%\GroupNB Clock\supervisor.log`.

## Run from source

```powershell
dotnet run --project src\Gnb.Clocking.App -f net9.0-windows10.0.19041.0
```

## Publish

Close the app first (Ctrl+Shift+Q), otherwise the exe can't be overwritten. The kiosk also keeps a hidden watcher running; that key stops both.

```powershell
dotnet publish src\Gnb.Clocking.App\Gnb.Clocking.App.csproj -f net9.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true -o publish\kiosk
```

## Run the published build

```powershell
.\publish\kiosk\Gnb.Clocking.App.exe
```

To install on another kiosk PC, copy the whole `publish\kiosk` folder.

## Staying open

The close button and Alt+F4 do nothing. The display and the PC are asked to stay awake, and a hidden watcher opens the clock again if the process exits or the window stops answering for 3 minutes. A published exe also registers itself to start when that Windows user signs in, so a reboot brings the clock back. Ctrl+Shift+Q is the way to stop it for an update.

## Keys

| Key | Action             |
| --- | ------------------ |
| F11 | Toggle full screen |
| F5  | Reset camera       |
| Ctrl+Shift+Q | Stop the kiosk so it can be updated |

## Tests

```powershell
dotnet test tests\Gnb.Clocking.Tests
```
