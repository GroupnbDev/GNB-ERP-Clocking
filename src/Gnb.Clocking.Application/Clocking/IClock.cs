namespace Gnb.Clocking.Application.Clocking;

public interface IClock
{
    DateTimeOffset Now { get; }
}
