using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
