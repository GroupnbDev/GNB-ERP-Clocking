using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.Infrastructure.ClockKiosk.Offline;

/// <summary>Outcome of one drain, for the screen.</summary>
public sealed record SyncOutcome(int Synced, int Parked, int Pending, bool Online, int Duplicates = 0);

/// <summary>
/// Sends queued punches once the link is back: photo first, then the punch, carrying the original
/// capture time and the device punch id. Order is the order people tapped. A punch the rules refuse
/// is parked and never retried; a punch that fails because the link is still down stays pending and
/// stops the drain, so nothing is lost and nothing is sent twice.
/// </summary>
public sealed class ClockSyncWorker
{
    /// <summary>Synced punches are kept this long so the screen can show what went through.</summary>
    public static readonly TimeSpan SyncedRetention = TimeSpan.FromDays(3);

    private readonly ClockKioskApiClient _api;
    private readonly OfflineClockStore _store;
    private readonly SemaphoreSlim _drain = new(1, 1);

    public ClockSyncWorker(ClockKioskApiClient api, OfflineClockStore store)
    {
        _api = api;
        _store = store;
    }

    public async Task<SyncOutcome> DrainAsync(CancellationToken cancellationToken = default)
    {
        if (!await _drain.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new SyncOutcome(0, 0, await _store.CountPendingAsync().ConfigureAwait(false), true);

        var synced = 0;
        var parked = 0;
        var duplicates = 0;
        var online = true;
        try
        {
            List<QueuedPunch> pending = await _store.ListPendingAsync().ConfigureAwait(false);
            // Tracks what this device last got accepted per candidate, to tell a double tap from a real gap.
            var lastWasClockOut = new Dictionary<int, bool>();
            foreach (QueuedPunch punch in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await SendAsync(punch, cancellationToken).ConfigureAwait(false);
                    await _store.MarkSyncedAsync(punch, DateTimeOffset.Now).ConfigureAwait(false);
                    lastWasClockOut[punch.CandidateId] = punch.IsClockOut;
                    synced++;
                }
                catch (ClockOfflineException)
                {
                    // Still unreachable. Keep the rest queued, in order, for the next attempt.
                    await _store.RecordAttemptAsync(punch, "Waiting for the clock server").ConfigureAwait(false);
                    online = false;
                    break;
                }
                catch (ClockingException ex)
                {
                    // The server answered and refused it. A retry would be refused the same way.
                    // A second clock-out for a shift this device already closed is a double tap, not a
                    // lost punch: still recorded on both sides, but it is not staff's problem.
                    bool duplicateClockOut = punch.IsClockOut
                        && lastWasClockOut.TryGetValue(punch.CandidateId, out bool alreadyOut)
                        && alreadyOut
                        && ex.Message.Contains("not clocked in", StringComparison.OrdinalIgnoreCase);
                    if (duplicateClockOut)
                    {
                        await _store.MarkDuplicateAsync(punch, ex.Message).ConfigureAwait(false);
                        duplicates++;
                    }
                    else
                    {
                        await _store.MarkParkedAsync(punch, ex.Message).ConfigureAwait(false);
                        parked++;
                    }
                }
            }

            if (online)
                await PruneAsync().ConfigureAwait(false);
        }
        finally
        {
            _drain.Release();
        }

        return new SyncOutcome(
            synced,
            parked,
            await _store.CountPendingAsync().ConfigureAwait(false),
            online,
            duplicates);
    }

    /// <summary>Refreshes the roster and measures how far the device clock is from the server's.</summary>
    public async Task RefreshRosterAsync(CancellationToken cancellationToken = default)
    {
        RosterResponse roster = await _api.GetRosterAsync(cancellationToken).ConfigureAwait(false);
        var before = DateTimeOffset.UtcNow;
        List<RosterEntry> entries = (roster.Candidates ?? new List<RosterCandidate>())
            .Select(c => new RosterEntry
            {
                CardNumber = Gnb.Clocking.Domain.Clocking.RfidNormalizer.Normalize(c.CardNumber),
                CandidateId = c.CandidateId,
                CandidateNumber = c.CandidateNumber,
                FirstName = c.FirstName ?? string.Empty,
                LastName = c.LastName ?? string.Empty,
                TenantId = c.TenantId,
                OrganizationId = c.OrganizationId,
                Assignment = c.Assignment ?? string.Empty,
                ClientName = c.ClientName ?? string.Empty,
            })
            .Where(e => e.CardNumber.Length > 0)
            .ToList();

        await _store.ReplaceRosterAsync(entries, DateTimeOffset.Now).ConfigureAwait(false);

        var skew = (int)Math.Round((before - roster.ServerTime).TotalSeconds);
        await _store.SetSettingAsync(OfflineClockStore.ClockSkewKey, skew.ToString()).ConfigureAwait(false);
    }

    private async Task SendAsync(QueuedPunch punch, CancellationToken cancellationToken)
    {
        var imagePath = punch.PhotoRelativePath;
        if (File.Exists(punch.PhotoPath))
        {
            PhotoUploadResponse uploaded = await _api
                .UploadPhotoAsync(punch.CardNumber, punch.IsClockOut, punch.PhotoPath, punch.LocalTime, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(uploaded.RelativePath))
                imagePath = uploaded.RelativePath!.Replace('\\', '/').TrimStart('/');
        }

        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ClockingException("The clock photo for this punch is missing from this device.");

        await _api.ClockAsync(
                punch.IsClockOut,
                punch.CardNumber,
                imagePath,
                resetCompletedDay: false,
                punch.ClientPunchId,
                punch.LocalTime,
                capturedOffline: true,
                punch.ClockSkewSeconds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PruneAsync()
    {
        List<QueuedPunch> old = await _store
            .TakeSyncedOlderThanAsync(DateTimeOffset.Now - SyncedRetention)
            .ConfigureAwait(false);
        foreach (QueuedPunch punch in old)
            await _store.DeleteAsync(punch).ConfigureAwait(false);
    }
}
