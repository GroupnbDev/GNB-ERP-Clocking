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
ClockKiosk__CameraMaxPhotoAgeMs=1000
ClockKiosk__CameraDailyRebuildAt=03:00
ClockKiosk__LogRetentionDays=14
```

`.env` is built into the exe, so rebuild or republish after you change it.

Logs land in `{AppData}/logs/kiosk-yyyyMMdd.log`. Leave `CameraDailyRebuildAt` empty to skip the nightly camera restart.

## Run from source

```powershell
dotnet run --project src\Gnb.Clocking.App -f net9.0-windows10.0.19041.0
```

## Publish

Close the app first (Ctrl+Shift+Q), otherwise the exe can't be overwritten. The kiosk also keeps a hidden watcher running; that key stops both.

```powershell
dotnet publish src\Gnb.Clocking.App\Gnb.Clocking.App.csproj -f net9.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true
```

## Run the published build

```powershell
.\publish\kiosk\Gnb.Clocking.App.exe
```

To install on another kiosk PC, copy the whole `publish\kiosk` folder.

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
