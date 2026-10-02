using Gnb.Clocking.Application.Clocking;

namespace Gnb.Clocking.App.Camera;

public sealed class MauiClockCamera : IClockCamera
{
    private ClockCameraView? _view;
    private IClockCameraHandler? _bound;

    public CameraHealth Health { get; private set; } = CameraHealth.Starting;

    public event Action<CameraHealth>? HealthChanged;

    public void Attach(ClockCameraView view)
    {
        if (_view != null)
            _view.HandlerChanged -= OnHandlerChanged;

        _view = view;
        view.HandlerChanged += OnHandlerChanged;
        BindHandler();
    }

    public Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken = default)
    {
        if (_view == null)
            throw new ClockingException("The camera preview is not on screen.");

        return _view.CaptureJpegAsync(cancellationToken);
    }

    public void ReportFailure(Exception error)
    {
        if (_view?.Handler is IClockCameraHandler camera)
            camera.ReportFailure(error);
    }

    public Task RebuildAsync(string trigger)
    {
        if (_view?.Handler is IClockCameraHandler camera)
            return camera.RebuildAsync(trigger);

        return Task.FromException(new ClockingException("The camera preview is not on screen."));
    }

    private void OnHandlerChanged(object? sender, EventArgs e) => BindHandler();

    private void BindHandler()
    {
        if (_bound != null)
            _bound.HealthChanged -= OnHealth;

        _bound = _view?.Handler as IClockCameraHandler;
        if (_bound == null)
            return;

        _bound.HealthChanged += OnHealth;
        OnHealth(_bound.Health);
    }

    private void OnHealth(CameraHealth health)
    {
        Health = health;
        HealthChanged?.Invoke(health);
    }
}
