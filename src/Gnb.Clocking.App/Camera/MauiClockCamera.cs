using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.App.Camera;

public sealed class MauiClockCamera : IClockCamera
{
    private ClockCameraView? _view;

    public void Attach(ClockCameraView view) => _view = view;

    public Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken = default)
    {
        if (_view == null)
            throw new ClockingException("The camera preview is not on screen.");

        return _view.CaptureJpegAsync(cancellationToken);
    }
}
