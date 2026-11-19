using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.Infrastructure.Photos;

namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// Writes the JPEG locally (<see cref="FileClockPhotoStore"/>), then <c>POST api/clock-kiosk/photos</c>.
/// The server picks the schedule day and file name (clock-out follows the open shift), so the local copy is moved
/// to the server's returned path (user clock-photo path) when it differs.
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
        StaffBadge staff,
        bool isClockOut,
        DateTime referenceDate,
        ReadOnlyMemory<byte> jpeg,
        CancellationToken cancellationToken = default)
    {
        var local = await _local.SaveJpegAsync(staff, isClockOut, referenceDate, jpeg, cancellationToken)
            .ConfigureAwait(false);

        PhotoUploadResponse uploaded;
        try
        {
            uploaded = await _api
                .UploadPhotoAsync(staff.Rfid, isClockOut, local.AbsolutePath, DateTimeOffset.Now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClockOfflineException)
        {
            // The JPEG is on disk. The queued punch uploads it when the link is back.
            return local;
        }

        // The server names the file; the kiosk only mirrors that path locally and never builds one for the server.
        var serverPath = uploaded.RelativePath?.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(serverPath) || serverPath.Contains("..", StringComparison.Ordinal))
            throw new ClockingException("The clock server did not store the photo. Try again.");

        if (string.Equals(serverPath, local.RelativePath, StringComparison.Ordinal))
            return local with { OnServer = true };

        var absolute = ClockKioskPhotoPaths.LocalAbsolutePath(_root, staff.UserId, serverPath);
        var folder = Path.GetDirectoryName(absolute);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        File.Move(local.AbsolutePath, absolute, overwrite: true);
        MoveThumbnail(local.AbsolutePath, absolute);
        return new ClockPhoto(serverPath, absolute, OnServer: true);
    }

    /// <summary>The activity list reads the thumbnail beside the photo, so it follows the photo's new folder.</summary>
    private static void MoveThumbnail(string fromPhoto, string toPhoto)
    {
        var from = ClockPhotoThumbnail.PathFor(fromPhoto);
        try
        {
            if (File.Exists(from))
                File.Move(from, ClockPhotoThumbnail.PathFor(toPhoto), overwrite: true);
            else
                ClockPhotoThumbnail.TryWrite(toPhoto);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The punch and its photo are saved; a missing thumbnail only shows initials in the list.
        }
    }
}
