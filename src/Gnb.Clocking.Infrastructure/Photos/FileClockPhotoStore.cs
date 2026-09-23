using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;

namespace Gnb.Clocking.Infrastructure.Photos;

public sealed class FileClockPhotoStore : IClockPhotoStore
{
    private readonly string _root;

    public FileClockPhotoStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A photo folder is required.", nameof(root));

        _root = root;
    }

    public async Task<ClockPhoto> SaveJpegAsync(
        CandidateBadge candidate,
        bool isClockOut,
        DateTime referenceDate,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        var candidateId = candidate?.CandidateId ?? 0;
        if (candidateId <= 0)
            throw new ClockingException("A candidate is required for the clock photo.");
        if (jpeg.IsEmpty)
            throw new ClockingException("A photo is required to clock in or out.");

        var relative = ClockPhotoPaths.RelativePath(referenceDate, isClockOut);
        var absolute = Path.Combine(
            _root,
            "Candidates",
            candidateId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            relative.Replace('/', Path.DirectorySeparatorChar));

        var folder = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        await File.WriteAllBytesAsync(absolute, jpeg.ToArray(), cancellationToken).ConfigureAwait(false);
        return new ClockPhoto(relative, absolute);
    }
}
