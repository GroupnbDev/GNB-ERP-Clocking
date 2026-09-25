using SQLite;

namespace Gnb.Clocking.Infrastructure.ClockKiosk.Offline;

public static class QueuedPunchStates
{
    /// <summary>Captured on the device, not yet accepted by the API.</summary>
    public const string Pending = "pending";

    /// <summary>The API accepted it. Kept briefly so the kiosk can show what synced.</summary>
    public const string Synced = "synced";

    /// <summary>The punch rules refused it on sync. Needs a person, never retried on its own.</summary>
    public const string Parked = "parked";

    /// <summary>
    /// A second clock-out for a shift this device already closed. The server was still asked, so the
    /// attempt is on record there, but it is not staff's problem: it is a double tap, not a lost punch.
    /// </summary>
    public const string Duplicate = "duplicate";
}

/// <summary>
/// A punch captured while the API was unreachable. <see cref="ClientPunchId"/> is what stops a
/// retry from punching twice: the API writes it once and replays that result afterwards.
/// </summary>
[Table("queued_punch")]
public class QueuedPunch
{
    [PrimaryKey]
    [Column("client_punch_id")]
    public string ClientPunchId { get; set; } = string.Empty;

    [Indexed]
    [Column("candidate_id")]
    public int CandidateId { get; set; }

    [Column("card_number")]
    public string CardNumber { get; set; } = string.Empty;

    [Column("candidate_name")]
    public string CandidateName { get; set; } = string.Empty;

    [Column("assignment")]
    public string Assignment { get; set; } = string.Empty;

    [Column("is_clock_out")]
    public bool IsClockOut { get; set; }

    /// <summary>
    /// When the person tapped, ISO 8601 with the device offset — stored as text on purpose.
    /// sqlite-net persists DateTimeOffset as UTC ticks and drops the offset, and the API writes the
    /// punch wall clock from that offset, so a lost offset would file the punch at the wrong hour.
    /// </summary>
    [Column("local_time")]
    public string LocalTimeIso { get; set; } = string.Empty;

    [Ignore]
    public DateTimeOffset LocalTime
    {
        get => DateTimeOffset.TryParse(
            LocalTimeIso,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value
            : CapturedAt;
        set => LocalTimeIso = value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Absolute path of the JPEG on this device. Uploaded at sync time.</summary>
    [Column("photo_path")]
    public string PhotoPath { get; set; } = string.Empty;

    [Column("photo_relative_path")]
    public string PhotoRelativePath { get; set; } = string.Empty;

    [Indexed]
    [Column("state")]
    public string State { get; set; } = QueuedPunchStates.Pending;

    [Column("attempts")]
    public int Attempts { get; set; }

    [Column("last_error")]
    public string? LastError { get; set; }

    /// <summary>Device clock minus server clock at the last sync, in seconds.</summary>
    [Column("clock_skew_seconds")]
    public int ClockSkewSeconds { get; set; }

    [Column("captured_at")]
    public DateTimeOffset CapturedAt { get; set; }

    [Column("synced_at")]
    public DateTimeOffset? SyncedAt { get; set; }
}

/// <summary>A candidate the kiosk may clock while the link is down. Refreshed from the API when online.</summary>
[Table("roster_entry")]
public class RosterEntry
{
    /// <summary>Normalized card number (uppercase letters and digits).</summary>
    [PrimaryKey]
    [Column("card_number")]
    public string CardNumber { get; set; } = string.Empty;

    [Column("candidate_id")]
    public int CandidateId { get; set; }

    [Column("candidate_number")]
    public int CandidateNumber { get; set; }

    [Column("first_name")]
    public string FirstName { get; set; } = string.Empty;

    [Column("last_name")]
    public string LastName { get; set; } = string.Empty;

    [Column("tenant_id")]
    public int TenantId { get; set; }

    [Column("organization_id")]
    public int OrganizationId { get; set; }

    [Column("assignment")]
    public string Assignment { get; set; } = string.Empty;

    [Column("client_name")]
    public string ClientName { get; set; } = string.Empty;
}

/// <summary>Small key/value store: roster freshness and the measured device clock skew.</summary>
[Table("kiosk_setting")]
public class KioskSetting
{
    [PrimaryKey]
    [Column("key")]
    public string Key { get; set; } = string.Empty;

    [Column("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Who was on shift when the server was last reachable. Persisted because the in-memory cache dies with
/// the app: restarting during an outage used to lose this, and the next scan became a clock-in — which the
/// server then filed as extra time instead of closing the shift.
/// </summary>
[Table("open_shift")]
public class OpenShiftRecord
{
    [PrimaryKey]
    [Column("candidate_id")]
    public int CandidateId { get; set; }

    /// <summary>ISO 8601 with offset, for the same reason the queued tap time is text.</summary>
    [Column("open_clock_in")]
    public string OpenClockInIso { get; set; } = string.Empty;

    [Ignore]
    public DateTimeOffset? OpenClockIn =>
        DateTimeOffset.TryParse(
            OpenClockInIso,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value
            : null;
}
