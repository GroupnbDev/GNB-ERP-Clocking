namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// The clock server could not be reached, or is not configured. Distinct from
/// <see cref="ClockingException"/>, which means the server answered and refused the punch:
/// this one is retryable, so the kiosk queues the punch instead of turning the person away.
/// </summary>
public sealed class ClockOfflineException : ClockingException
{
    public ClockOfflineException(string message) : base(message)
    {
    }
}
