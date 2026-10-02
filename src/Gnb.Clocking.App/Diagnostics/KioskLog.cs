using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gnb.Clocking.App.Diagnostics;

/// <summary>
/// Loggers for types MAUI constructs itself (camera handler, page). Set from <c>MauiProgram</c>
/// once the app's <see cref="ILoggerFactory"/> exists.
/// </summary>
public static class KioskLog
{
    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;

    public static ILogger<T> Create<T>() => Factory.CreateLogger<T>();
}
