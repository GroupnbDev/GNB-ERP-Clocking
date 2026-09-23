# Prompt: GroupNB Clock API for the MAUI kiosk

Use this prompt in a new agent session with both repositories open:

- API (edit here): `/Users/groupnb/Desktop/Git/GNB SAAS/gnbSaasApi`
- MAUI app (edit here after the API exists): `/Users/groupnb/Desktop/Git/GNB ERP Clocking`

Before exploring `gnbSaasApi`, follow `/Users/groupnb/Desktop/Git/GNB SAAS/.cursor/rules/graphify.mdc` and run `graphify query` from the `gnbSaasApi` directory.

---

You are adding a kiosk clocking API to `gnbSaasApi` and then switching the .NET MAUI app `Gnb.Clocking` from its in-memory preview to that API.

The MAUI app is a Mac and Windows RFID time clock. A USB reader types a badge id and presses Enter. The app rejects slow typing. A scan opens the camera. A punch is saved only when a JPEG was captured. There is no screen for typing a clock-in or clock-out time, and no button that punches without a badge.

## What already exists in gnbSaasApi

Reuse these. Do not invent a second punch model.

- `CandidatePortalWorkService.ClockInAsync` and `ClockOutAsync` write `punch_card_records.clock_in` / `clock_out`, `location_in` / `location_out`, and `hours_worked` on the existing day row. Clock-in does not insert another row when that calendar day already exists. A later clock-in reopens that day only when `reset_completed_day` is true, after the kiosk asks the candidate. Clock-out closes the one open shift. A second clock-in while a shift is open fails. Clock-out with no open shift fails.
- `UploadClockPhotoAsync` and `AlignClockPhotoToRecordDayAsync` store the photo on disk, not in a punch-card column. The canonical relative path, from `PortalTimesheetDocumentHelper`, is:
  - `Candidates/{candidateId}/Records/{yyyy-MM-dd}/ClockIN.jpeg`
  - `Candidates/{candidateId}/Records/{yyyy-MM-dd}/ClockOut.jpeg`
  Clock-out uses the open punch row’s `reference_date`, including overnight shifts.
- `POST /portal/timesheet/photos` then `POST /portal/timesheet/clock-in` or `clock-out` with `image_path`. Those routes require a candidate portal user. The kiosk is a shared device, so do not make the Mac app log in as each candidate.
- `GroupNBSettingsOptions.IsAllowed` allows a row only when `tenant_id` is in `GroupNBSettings:TenantIds` and `organization_id` is in `GroupNBSettings:OrganizationIds`. For this kiosk those lists are `[6, 1]` and `[16, 2]`.
- `candidates` has `id`, `candidate_number`, `first_name`, `last_name`, `tenant_id`, `organization_id`. There is no RFID column today. Add the badge link. Do not overload an unrelated field.

## API to add

Add a kiosk controller, for example `Controllers/ClockKioskController.cs`, route prefix `api/clock-kiosk`. Keep portal routes unchanged.

Authenticate the device with a configured kiosk key (`ClockKiosk:ApiKey`), sent as `X-Clock-Kiosk-Key`. Reject missing or wrong keys. Do not put the recruitment database password or the kiosk key in source control. Read them from configuration / environment:

- `ConnectionStrings__DefaultConnection` (already the recruitment PostgreSQL database)
- `GroupNBSettings__TenantIds=[6,1]`
- `GroupNBSettings__OrganizationIds=[16,2]`
- `ClockKiosk__ApiKey`

Every candidate lookup and punch must call `GroupNBSettingsOptions.IsAllowed`. A badge whose candidate is outside that allowlist is unknown. Return the same shape as an unknown badge. Do not reveal that the person exists in another tenant.

### Data

Add a tenant-scoped badge link, table `candidate_rfid_badges`:

- `id`, `tenant_id`, `organization_id`, `candidate_id`
- `rfid` stored normalized: uppercase letters and digits only (same rule as `Gnb.Clocking.Domain.Clocking.RfidNormalizer`)
- unique `(tenant_id, rfid)` among non-deleted rows
- standard `BaseModel` audit columns

One candidate can have one active badge per tenant. Scanning that badge resolves `candidates.id`.

### Endpoints

1. `GET api/clock-kiosk/badges/{rfid}`

   Normalize `rfid`. Return 404 when no in-scope candidate is linked. Return:

   ```json
   {
     "candidate_id": 1042,
     "candidate_number": 42,
     "first_name": "Maya",
     "last_name": "Chen",
     "rfid": "04A1C8E291",
     "tenant_id": 6,
     "organization_id": 16,
     "is_on_shift": false,
     "open_clock_in": null,
     "reference_date": null,
     "assignment": "Inbound sort",
     "client_name": "Sysco",
     "site": "Brampton DC",
     "punch_card_id": null
   }
   ```

   `assignment`, `client_name`, and `site` come from the editable punch card / demand / client the portal clock would use. `is_on_shift` is true when `FindOpenShiftAsync` would find an open `punch_card_records` row.

2. `POST api/clock-kiosk/photos`

   Multipart form: `photo` (JPEG), `rfid`, `is_clock_out`. Resolve the candidate from the badge first. Save through the existing clock-photo pipeline so the file lands at `Records/{reference_date}/ClockIN.jpeg` or `ClockOut.jpeg`. For clock-out, `reference_date` is the open shift’s `reference_date`. Response:

   ```json
   { "relative_path": "Records/2026-09-22/ClockIN.jpeg", "file_name": "ClockIN.jpeg" }
   ```

   Empty or missing file: 400 `A photo is required to clock in or out.`

3. `POST api/clock-kiosk/clock-in` and `POST api/clock-kiosk/clock-out`

   JSON body:

   ```json
   { "rfid": "04A1C8E291", "image_path": "Records/2026-09-22/ClockIN.jpeg", "local_time": "2026-09-22T16:01:00+08:00" }
   ```

   `image_path` is required and must be the canonical clock photo for that action. `local_time` is the kiosk device time with its UTC offset. The API keeps that offset for the punchcard wall clock (so 16:01 stays 16:01) and still uses the server clock for the instant. Delegate the punch write to the existing portal clock methods (or a shared internal method they both call) so hours, status, and the open-shift rules stay identical. The kiosk has no browser geolocation. Pass a station location from `ClockKiosk:Latitude`, `ClockKiosk:Longitude`, and `ClockKiosk:LocationLabel` into `location_in` / `location_out` instead of rejecting the call for a missing device GPS fix.

   Response matches `PortalClockActionResponseDto` plus `candidate_id` and `image_path`. Map the existing failures to 400:

   - already clocked in
   - not clocked in
   - no active schedule
   - photo required
   - candidate outside GroupNB allowlist (404, same as unknown badge)

4. `GET api/clock-kiosk/sessions?take=50`

   Recent clock events for in-scope candidates at this kiosk’s tenant scope: candidate name, action `clock_in` or `clock_out`, local time, assignment, hours on clock-out, and the photo relative path when the file exists (`PortalTimesheetDocumentHelper.ResolveClockPhotoPathsForRecord`).

Add tests beside the existing portal clock tests. Cover: in-scope badge resolves to `candidate_id`; out-of-scope badge is 404; clock-in then clock-out 90 minutes later stores 1.5 hours; second clock-in is rejected; clock-out with no open shift is rejected; missing photo is rejected; photo path is `Records/{day}/ClockIN.jpeg` or `ClockOut.jpeg`.

Run `graphify update .` in `gnbSaasApi` after the API change.

## Then wire the MAUI app

Repository: `/Users/groupnb/Desktop/Git/GNB ERP Clocking`. Solution `Gnb.Clocking.sln`.

Replace the preview registrations in `ClockingServiceCollectionExtensions` / `MauiProgram` with HTTP implementations of the existing ports. Keep the UI, RFID burst detection, and camera flow.

- `ICandidateBadgeDirectory.FindByRfidAsync` calls `GET api/clock-kiosk/badges/{rfid}`.
- `ListLinkedBadges` returns an empty list. The kiosk no longer shows sample people.
- `IClockPhotoStore.SaveJpegAsync` still writes the JPEG locally, then `POST api/clock-kiosk/photos` with that file. Keep using `ClockPhotoPaths` (`ClockIN.jpeg` / `ClockOut.jpeg`).
- `IClockingService.ClockInAsync` / `ClockOutAsync` call the matching kiosk endpoints with `rfid` and `image_path`. Surface API 400 messages through `ClockingException` so the kiosk banner shows them.
- `GetEvents` / `GetOpenClockIn` / `OpenCount` come from `GET api/clock-kiosk/sessions` and the badge lookup. Remove the seeded Aisha preview punch.
- Base URL and kiosk key from MAUI configuration: `ClockKiosk:BaseUrl`, `ClockKiosk:ApiKey`. Send `X-Clock-Kiosk-Key`. Do not commit the key or the database password.
- Leave `MauiClockCamera` as the capture implementation.

The screen stays RFID-only: scan, camera, punch. Do not add a text field, sample chips, or a manual clock button.

Build the API tests and `dotnet build src/Gnb.Clocking.App/Gnb.Clocking.App.csproj -f net9.0-maccatalyst`. On Windows the app target is `net9.0-windows10.0.19041.0`.
