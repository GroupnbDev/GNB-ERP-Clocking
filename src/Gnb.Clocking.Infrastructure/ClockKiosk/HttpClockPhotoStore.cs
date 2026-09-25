using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.Photos;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// Writes the JPEG locally (<see cref="FileClockPhotoStore"/>), then <c>POST api/clock-kiosk/photos</c>.
/// The server picks the schedule day (clock-out follows the open shift), so the local copy is moved
/// to the returned <c>Records/{day}/ClockIN|ClockOut.jpeg</c> when the days differ.
/// </summary>
public sealed class HttpClockPhotoStore : IClockPhotoStore
{
    private readonly FileClockPhotoStore _local;
    private readonly ClockKioskApiClient _api;
    private readonly string _root;

    public HttpClockPhotoStore(FileClockPhotoStore local, ClockKioskApiClient api, ClockKioskApiOptions options)
    {
        _local = local;
        _api = api;
        _root = options.PhotoRoot;
    }

    public async Task<ClockPhoto> SaveJpegAsync(
        CandidateBadge candidate,
        bool isClockOut,
        DateTime referenceDate,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        var local = await _local.SaveJpegAsync(candidate, isClockOut, referenceDate, jpeg, cancellationToken)
            .ConfigureAwait(false);

        PhotoUploadResponse uploaded;
        try
        {
            uploaded = await _api
                .UploadPhotoAsync(candidate.Rfid, isClockOut, local.AbsolutePath, DateTimeOffset.Now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClockOfflineException)
        {
            // The JPEG is on disk. The queued punch uploads it when the link is back.
            return local;
        }

        var serverPath = uploaded.RelativePath?.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(serverPath)
            || !serverPath.EndsWith(ClockPhotoPaths.FileName(isClockOut), StringComparison.Ordinal))
            throw new ClockingException("The clock server did not store the photo. Try again.");

        if (string.Equals(serverPath, local.RelativePath, StringComparison.Ordinal))
            return local;

        var absolute = ClockKioskPhotoPaths.LocalAbsolutePath(_root, candidate.CandidateId, serverPath);
        var folder = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        File.Move(local.AbsolutePath, absolute, overwrite: true);
        return new ClockPhoto(serverPath, absolute);
    }
}
