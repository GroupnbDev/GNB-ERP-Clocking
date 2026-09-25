using SQLite;

namespace Gnb.Clocking.Infrastructure.ClockKiosk.Offline;

/// <summary>
/// The device's durable state: punches waiting to sync, the cached roster, and the measured clock skew.
/// SQLite so a power cut mid-write cannot lose a punch or leave half of one behind.
/// </summary>
public sealed class OfflineClockStore : IAsyncDisposable
{
    public const string RosterSyncedAtKey = "roster_synced_at";
    public const string ClockSkewKey = "clock_skew_seconds";

    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private bool _initialized;

    public OfflineClockStore(string databasePath)
    {
        _db = new SQLiteAsyncConnection(
            databasePath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex | SQLiteOpenFlags.SharedCache);
    }

    private async Task<SQLiteAsyncConnection> ConnectionAsync()
    {
        if (_initialized)
            return _db;

        await _ready.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_initialized)
            {
                await _db.CreateTableAsync<QueuedPunch>().ConfigureAwait(false);
                await _db.CreateTableAsync<RosterEntry>().ConfigureAwait(false);
                await _db.CreateTableAsync<OpenShiftRecord>().ConfigureAwait(false);
                await _db.CreateTableAsync<KioskSetting>().ConfigureAwait(false);
                _initialized = true;
            }
        }
        finally
        {
            _ready.Release();
        }

        return _db;
    }

    // ---------------------------------------------------------------- queue

    public async Task EnqueueAsync(QueuedPunch punch)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.InsertOrReplaceAsync(punch).ConfigureAwait(false);
    }

    /// <summary>Pending punches oldest first — the order they were tapped is the order they sync.</summary>
    public async Task<List<QueuedPunch>> ListPendingAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>()
            .Where(p => p.State == QueuedPunchStates.Pending)
            .OrderBy(p => p.CapturedAt)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<QueuedPunch>> ListParkedAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>()
            .Where(p => p.State == QueuedPunchStates.Parked)
            .OrderBy(p => p.CapturedAt)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>Punches this device is still holding — shown on the live list so a shift is never invisible.</summary>
    public async Task<List<QueuedPunch>> ListUnsyncedAsync(int take)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>()
            .Where(p => p.State == QueuedPunchStates.Pending || p.State == QueuedPunchStates.Parked)
            .OrderByDescending(p => p.CapturedAt)
            .Take(take)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> CountPendingAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>().Where(p => p.State == QueuedPunchStates.Pending).CountAsync().ConfigureAwait(false);
    }

    public async Task<int> CountParkedAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>().Where(p => p.State == QueuedPunchStates.Parked).CountAsync().ConfigureAwait(false);
    }

    public async Task MarkSyncedAsync(QueuedPunch punch, DateTimeOffset syncedAt)
    {
        punch.State = QueuedPunchStates.Synced;
        punch.SyncedAt = syncedAt;
        punch.LastError = null;
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.UpdateAsync(punch).ConfigureAwait(false);
    }

    /// <summary>The API refused it. Keep it, stop retrying, and let the screen say so.</summary>
    public async Task MarkParkedAsync(QueuedPunch punch, string reason)
    {
        punch.State = QueuedPunchStates.Parked;
        punch.LastError = reason;
        punch.Attempts += 1;
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.UpdateAsync(punch).ConfigureAwait(false);
    }

    /// <summary>A double tap the server refused. Recorded, but not counted as needing attention.</summary>
    public async Task MarkDuplicateAsync(QueuedPunch punch, string reason)
    {
        punch.State = QueuedPunchStates.Duplicate;
        punch.LastError = reason;
        punch.Attempts += 1;
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.UpdateAsync(punch).ConfigureAwait(false);
    }

    public async Task RecordAttemptAsync(QueuedPunch punch, string? error)
    {
        punch.Attempts += 1;
        punch.LastError = error;
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.UpdateAsync(punch).ConfigureAwait(false);
    }

    /// <summary>Most recent pending punch for a candidate — the device's own view of their shift state.</summary>
    public async Task<QueuedPunch?> LatestPendingForCandidateAsync(int candidateId)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        List<QueuedPunch> rows = await db.Table<QueuedPunch>()
            .Where(p => p.CandidateId == candidateId && p.State == QueuedPunchStates.Pending)
            .OrderByDescending(p => p.CapturedAt)
            .ToListAsync()
            .ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    /// <summary>Drops synced punches older than the window so the device does not grow without bound.</summary>
    public async Task<List<QueuedPunch>> TakeSyncedOlderThanAsync(DateTimeOffset cutoff)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<QueuedPunch>()
            .Where(p => p.State == QueuedPunchStates.Synced && p.SyncedAt < cutoff)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(QueuedPunch punch)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.DeleteAsync(punch).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- open shifts

    /// <summary>Snapshot of who the server says is on shift, kept so a restart during an outage remembers.</summary>
    public async Task ReplaceOpenShiftsAsync(IEnumerable<KeyValuePair<int, DateTimeOffset>> openShifts)
    {
        var rows = openShifts
            .Select(pair => new OpenShiftRecord
            {
                CandidateId = pair.Key,
                OpenClockInIso = pair.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            })
            .ToList();

        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.RunInTransactionAsync(tran =>
        {
            tran.DeleteAll<OpenShiftRecord>();
            foreach (OpenShiftRecord row in rows)
                tran.InsertOrReplace(row);
        }).ConfigureAwait(false);
    }

    public async Task<OpenShiftRecord?> FindOpenShiftAsync(int candidateId)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<OpenShiftRecord>()
            .Where(r => r.CandidateId == candidateId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<OpenShiftRecord>> ListOpenShiftsAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<OpenShiftRecord>().ToListAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- roster

    public async Task ReplaceRosterAsync(IReadOnlyList<RosterEntry> entries, DateTimeOffset syncedAt)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.RunInTransactionAsync(tran =>
        {
            tran.DeleteAll<RosterEntry>();
            foreach (RosterEntry entry in entries)
                tran.InsertOrReplace(entry);
        }).ConfigureAwait(false);
        await SetSettingAsync(RosterSyncedAtKey, syncedAt.ToString("o")).ConfigureAwait(false);
    }

    public async Task<RosterEntry?> FindRosterByCardAsync(string normalizedCardNumber)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<RosterEntry>()
            .Where(r => r.CardNumber == normalizedCardNumber)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> CountRosterAsync()
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        return await db.Table<RosterEntry>().CountAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- settings

    public async Task SetSettingAsync(string key, string value)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        await db.InsertOrReplaceAsync(new KioskSetting { Key = key, Value = value }).ConfigureAwait(false);
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        var db = await ConnectionAsync().ConfigureAwait(false);
        KioskSetting? row = await db.Table<KioskSetting>().Where(s => s.Key == key).FirstOrDefaultAsync().ConfigureAwait(false);
        return row?.Value;
    }

    public async Task<int> GetClockSkewSecondsAsync()
    {
        string? raw = await GetSettingAsync(ClockSkewKey).ConfigureAwait(false);
        return int.TryParse(raw, out int seconds) ? seconds : 0;
    }

    public async Task<DateTimeOffset?> GetRosterSyncedAtAsync()
    {
        string? raw = await GetSettingAsync(RosterSyncedAtKey).ConfigureAwait(false);
        return DateTimeOffset.TryParse(raw, out DateTimeOffset value) ? value : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _db.CloseAsync().ConfigureAwait(false);
        _ready.Dispose();
    }
}
