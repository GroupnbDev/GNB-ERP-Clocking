using System.Text.Json.Serialization;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

// Wire shapes of gnbSaasApi api/clock-kiosk (snake_case JSON).

internal sealed record BadgeResponse(
    [property: JsonPropertyName("candidate_id")] int CandidateId,
    [property: JsonPropertyName("candidate_number")] int CandidateNumber,
    [property: JsonPropertyName("card_number")] string? CardNumber,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("rfid")] string? Rfid,
    [property: JsonPropertyName("tenant_id")] int TenantId,
    [property: JsonPropertyName("organization_id")] int OrganizationId,
    [property: JsonPropertyName("is_on_shift")] bool IsOnShift,
    [property: JsonPropertyName("open_clock_in")] DateTimeOffset? OpenClockIn,
    [property: JsonPropertyName("reference_date")] string? ReferenceDate,
    [property: JsonPropertyName("assignment")] string? Assignment,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("site")] string? Site,
    [property: JsonPropertyName("punch_card_id")] int? PunchCardId,
    [property: JsonPropertyName("shift_finished")] bool ShiftFinished = false,
    [property: JsonPropertyName("finished_clock_in")] DateTimeOffset? FinishedClockIn = null,
    [property: JsonPropertyName("finished_clock_out")] DateTimeOffset? FinishedClockOut = null);

internal sealed record PhotoUploadResponse(
    [property: JsonPropertyName("relative_path")] string? RelativePath,
    [property: JsonPropertyName("file_name")] string? FileName);

internal sealed record ClockRequest(
    [property: JsonPropertyName("rfid")] string Rfid,
    [property: JsonPropertyName("image_path")] string ImagePath,
    [property: JsonPropertyName("reset_completed_day")] bool ResetCompletedDay = false,
    [property: JsonPropertyName("local_time")] DateTimeOffset? LocalTime = null,
    // The API writes one punch per id and replays that result, so a retry cannot punch twice.
    [property: JsonPropertyName("client_punch_id")] string? ClientPunchId = null,
    [property: JsonPropertyName("captured_offline")] bool CapturedOffline = false,
    [property: JsonPropertyName("device_clock_skew_seconds")] int? DeviceClockSkewSeconds = null);

internal sealed record RosterResponse(
    [property: JsonPropertyName("candidates")] List<RosterCandidate>? Candidates,
    [property: JsonPropertyName("server_time")] DateTimeOffset ServerTime,
    [property: JsonPropertyName("count")] int Count);

internal sealed record RosterCandidate(
    [property: JsonPropertyName("candidate_id")] int CandidateId,
    [property: JsonPropertyName("candidate_number")] int CandidateNumber,
    [property: JsonPropertyName("card_number")] string? CardNumber,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("tenant_id")] int TenantId,
    [property: JsonPropertyName("organization_id")] int OrganizationId,
    [property: JsonPropertyName("assignment")] string? Assignment,
    [property: JsonPropertyName("client_name")] string? ClientName);

internal sealed record ClockActionResponse(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("candidate_id")] int CandidateId,
    [property: JsonPropertyName("record_id")] int RecordId,
    [property: JsonPropertyName("hours_worked")] double? HoursWorked,
    [property: JsonPropertyName("utc_time")] DateTime UtcTime,
    [property: JsonPropertyName("demand_name")] string? DemandName,
    [property: JsonPropertyName("image_path")] string? ImagePath,
    // What the server actually recorded, which is not always what the kiosk asked for.
    [property: JsonPropertyName("is_extra")] bool IsExtra = false);

internal sealed record SessionsResponse(
    [property: JsonPropertyName("events")] List<SessionEvent>? Events,
    [property: JsonPropertyName("open_shifts")] List<OpenShift>? OpenShifts,
    [property: JsonPropertyName("open_count")] int OpenCount);

internal sealed record SessionEvent(
    [property: JsonPropertyName("record_id")] int RecordId,
    [property: JsonPropertyName("candidate_id")] int CandidateId,
    [property: JsonPropertyName("first_name")] string? FirstName,
    [property: JsonPropertyName("last_name")] string? LastName,
    [property: JsonPropertyName("candidate_name")] string? CandidateName,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("local_time")] DateTimeOffset LocalTime,
    [property: JsonPropertyName("assignment")] string? Assignment,
    [property: JsonPropertyName("hours_worked")] double? HoursWorked,
    [property: JsonPropertyName("photo_relative_path")] string? PhotoRelativePath);

internal sealed record OpenShift(
    [property: JsonPropertyName("candidate_id")] int CandidateId,
    [property: JsonPropertyName("open_clock_in")] DateTimeOffset OpenClockIn);

internal sealed record ApiError([property: JsonPropertyName("error")] string? Error);
