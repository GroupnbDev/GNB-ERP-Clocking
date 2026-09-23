using Gnb.Clocking.App.Camera;
using Gnb.Clocking.Application.Clocking;
using Microsoft.Maui.Handlers;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using WinPreviewElement = Microsoft.UI.Xaml.Controls.MediaPlayerElement;

namespace Gnb.Clocking.App.Platforms.Windows;

// WinUI 3 never got the classic UWP CaptureElement control (tracked upstream at
// microsoft/microsoft-ui-xaml#8214), so the camera preview is rendered through a
// MediaPlayerElement bound to a MediaFrameSource instead, per Microsoft's own guidance:
// https://learn.microsoft.com/windows/apps/develop/camera/camera-quickstart-winui3
public sealed class WinClockCameraHandler : ViewHandler<ClockCameraView, WinPreviewElement>, IClockCameraHandler
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
    private MediaPlayer? _mediaPlayer;
    private VideoEncodingProperties? _previewFormat;
    private string? _error;

    public WinClockCameraHandler() : base(PropertyMapper)
    {
    }

    protected override WinPreviewElement CreatePlatformView() => new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
        AreTransportControlsEnabled = false
    };

    protected override void ConnectHandler(WinPreviewElement platformView)
    {
        base.ConnectHandler(platformView);
        _ = StartAsync(platformView);
    }

    protected override void DisconnectHandler(WinPreviewElement platformView)
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _media?.Dispose();
        _media = null;

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

    private async Task StartAsync(WinPreviewElement view)
    {
        try
        {
            var media = new MediaCapture();
            await media.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video
            }).AsTask().ConfigureAwait(true);

            _previewFormat = await UseLightPreviewAsync(media).ConfigureAwait(true);

            var frameSource = FindColorFrameSource(media)
                ?? throw new ClockingException("No video preview or record stream found.");

            var player = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false,
                Source = MediaSource.CreateFromMediaFrameSource(frameSource)
            };
            view.SetMediaPlayer(player);
            player.Play();

            _mediaPlayer = player;
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

    /// <summary>Prefers the dedicated preview stream some drivers expose; falls back to the record stream.</summary>
    private static MediaFrameSource? FindColorFrameSource(MediaCapture media)
    {
        var preview = media.FrameSources.Values.FirstOrDefault(source =>
            source.Info.MediaStreamType == MediaStreamType.VideoPreview
            && source.Info.SourceKind == MediaFrameSourceKind.Color);
        if (preview != null)
            return preview;

        return media.FrameSources.Values.FirstOrDefault(source =>
            source.Info.MediaStreamType == MediaStreamType.VideoRecord
            && source.Info.SourceKind == MediaFrameSourceKind.Color);
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
