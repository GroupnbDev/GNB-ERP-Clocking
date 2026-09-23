using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Application.Clocking;

public interface IClockPhotoStore
{
    Task<ClockPhoto> SaveJpegAsync(
        CandidateBadge candidate,
        bool isClockOut,
        DateTime referenceDate,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default);
}
