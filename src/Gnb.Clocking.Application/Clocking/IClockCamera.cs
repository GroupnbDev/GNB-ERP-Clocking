namespace Gnb.Clocking.Application.Clocking;

public interface IClockCamera
{
    CameraHealth Health { get; }

    event Action<CameraHealth>? HealthChanged;

    /// <summary>
    /// Grabs a JPEG from the live kiosk camera. Returns null when the frame is empty.
    /// Throws when the camera is not live — a punch is never saved without a photo.
    /// </summary>
    Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken = default);

    /// <summary>The last photo grab failed. The platform camera rebuilds itself.</summary>
    void ReportFailure(Exception error);

    /// <summary>Drops the current session and opens the camera again. <paramref name="trigger"/> is written to the log.</summary>
    Task RebuildAsync(string trigger);
}
