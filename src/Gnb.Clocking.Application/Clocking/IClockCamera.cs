namespace Gnb.Clocking.Application.Clocking;

public interface IClockCamera
{
    /// <summary>
    /// Grabs a JPEG from the live kiosk camera. Returns null when the frame is empty.
    /// </summary>
    Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken = default);
}
