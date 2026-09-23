using Gnb.Clocking.App.Camera;
using Gnb.Clocking.Application.Clocking;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using WinCaptureElement = Microsoft.UI.Xaml.Controls.CaptureElement;

namespace Gnb.Clocking.App.Platforms.Windows;

public sealed class WinClockCameraHandler : ViewHandler<ClockCameraView, WinCaptureElement>, IClockCameraHandler
{
    public static IPropertyMapper<ClockCameraView, WinClockCameraHandler> PropertyMapper =
        new PropertyMapper<ClockCameraView, WinClockCameraHandler>(ViewMapper);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>
    /// Preview and photo size cap. A 1080p+ webcam stream costs an older i3 with integrated graphics a
    /// large share of its CPU just to decode and scale into a small circle; 720p is plenty for the circle
    /// and for an identity photo, and keeps each saved JPEG around 100–200 KB.
    /// </summary>
    private const uint MaxHeight = 720;
    private const double MaxFrameRate = 30;

    private MediaCapture? _media;
    private VideoEncodingProperties? _previewFormat;
    private string? _error;

    public WinClockCameraHandler() : base(PropertyMapper)
    {
    }

    protected override WinCaptureElement CreatePlatformView() => new()
    {
        Stretch = Stretch.UniformToFill
    };

    protected override void ConnectHandler(WinCaptureElement platformView)
    {
        base.ConnectHandler(platformView);
        _ = StartAsync(platformView);
    }

    protected override async void DisconnectHandler(WinCaptureElement platformView)
    {
        if (_media != null)
        {
            await _media.StopPreviewAsync();
            _media.Dispose();
            _media = null;
        }

        base.DisconnectHandler(platformView);
    }

    public async Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        if (_media == null)
            throw new ClockingException(_error ?? "The camera is not ready.");

        var encoding = ImageEncodingProperties.CreateJpeg();
        if (_previewFormat is { Width: > 0, Height: > 0 })
        {
            // Same frame size as the preview: no full-sensor capture, no extra resize work.
            encoding.Width = _previewFormat.Width;
            encoding.Height = _previewFormat.Height;
        }

        using var stream = new InMemoryRandomAccessStream();
        await _media.CapturePhotoToStreamAsync(encoding, stream).AsTask(cancellationToken).ConfigureAwait(true);
        stream.Seek(0);
        var size = (uint)stream.Size;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size).AsTask(cancellationToken).ConfigureAwait(true);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task StartAsync(WinCaptureElement view)
    {
        try
        {
            var media = new MediaCapture();
            await media.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video
            }).AsTask().ConfigureAwait(true);

            _previewFormat = await UseLightPreviewAsync(media).ConfigureAwait(true);
            view.Source = media;
            await media.StartPreviewAsync().AsTask().ConfigureAwait(true);
            _media = media;
            _ready.TrySetResult();
            VirtualView.SetStatus("Camera live");
        }
        catch (UnauthorizedAccessException)
        {
            Fail("Allow camera access to save clock photos.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>Picks the largest preview format at or under 720p and 30 fps, preferring uncompressed formats
    /// (no MJPEG decode on the CPU). Leaves the camera default when nothing fits.</summary>
    private static async Task<VideoEncodingProperties?> UseLightPreviewAsync(MediaCapture media)
    {
        try
        {
            var controller = media.VideoDeviceController;
            var best = controller.GetAvailableMediaStreamProperties(MediaStreamType.VideoPreview)
                .OfType<VideoEncodingProperties>()
                .Where(format => format.Height > 0 && format.Height <= MaxHeight && FrameRate(format) <= MaxFrameRate + 0.5)
                .OrderByDescending(format => format.Height)
                .ThenByDescending(format => FrameRate(format))
                .ThenBy(format => string.Equals(format.Subtype, "MJPG", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();

            if (best == null)
                return controller.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

            await controller.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, best).AsTask().ConfigureAwait(true);
            return best;
        }
        catch (Exception)
        {
            // Some drivers refuse format changes; the default preview still works.
            return null;
        }
    }

    private static double FrameRate(VideoEncodingProperties format) =>
        format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private void Fail(string message)
    {
        _error = message;
        _ready.TrySetResult();
        VirtualView?.SetStatus(message);
    }
}
