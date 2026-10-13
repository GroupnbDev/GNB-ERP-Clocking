namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// How often the kiosk looks for its watcher and relaunches it. The watcher only takes its mutex once
/// it has started, so checking every second on a slow laptop launched several watchers in a row.
/// </summary>
public static class SupervisorSpawnPolicy
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SpawnRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>The first call always checks; after that at most once per <see cref="CheckEvery"/>.</summary>
    public static bool ShouldCheck(TimeSpan? sinceLastCheck) =>
        sinceLastCheck is not TimeSpan elapsed || elapsed >= CheckEvery;

    /// <summary>A watcher launched less than <see cref="SpawnRetryAfter"/> ago may still be starting up.</summary>
    public static bool MaySpawn(TimeSpan? sinceLastSpawn) =>
        sinceLastSpawn is not TimeSpan elapsed || elapsed >= SpawnRetryAfter;
}
