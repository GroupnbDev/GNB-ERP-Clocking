namespace Gnb.Clocking.App.Camera;

public class ClockCameraView : View
{
    public static readonly BindableProperty StatusTextProperty = BindableProperty.Create(
        nameof(StatusText),
        typeof(string),
        typeof(ClockCameraView),
        "Starting camera");

    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public void SetStatus(string text)
    {
        if (MainThread.IsMainThread)
            StatusText = text;
        else
            MainThread.BeginInvokeOnMainThread(() => StatusText = text);
    }

    public Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken = default)
    {
        if (Handler is IClockCameraHandler camera)
            return camera.CaptureJpegAsync(cancellationToken);

        return Task.FromResult<byte[]?>(null);
    }
}

public interface IClockCameraHandler
{
    Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken);
}
