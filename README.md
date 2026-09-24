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
```

`.env` is built into the exe, so rebuild or republish after you change it.

## Run from source

```powershell
dotnet run --project src\Gnb.Clocking.App -f net9.0-windows10.0.19041.0
```

## Publish

Close the app first, otherwise the exe can't be overwritten.

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

## Tests

```powershell
dotnet test tests\Gnb.Clocking.Tests
```
