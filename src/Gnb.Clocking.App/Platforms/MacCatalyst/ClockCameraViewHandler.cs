using AVFoundation;
using CoreGraphics;
using Foundation;
using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Diagnostics;
using Gnb.Clocking.Application.Clocking;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Handlers;
using UIKit;

namespace Gnb.Clocking.App.Platforms.MacCatalyst;

public sealed class MacClockCameraHandler : ViewHandler<ClockCameraView, CameraPreviewView>, IClockCameraHandler
{
    public static IPropertyMapper<ClockCameraView, MacClockCameraHandler> PropertyMapper =
        new PropertyMapper<ClockCameraView, MacClockCameraHandler>(ViewMapper);

    private readonly ILogger<MacClockCameraHandler> _logger = KioskLog.Create<MacClockCameraHandler>();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AVCaptureSession? _session;
    private AVCapturePhotoOutput? _photoOutput;
    private JpegPhotoDelegate? _delegate;
    private string? _error;
    private CameraHealth _health = CameraHealth.Starting;

    public CameraHealth Health => _health;

    public event Action<CameraHealth>? HealthChanged;

    public MacClockCameraHandler() : base(PropertyMapper)
    {
    }

    protected override CameraPreviewView CreatePlatformView() => new();

    protected override void ConnectHandler(CameraPreviewView platformView)
    {
        base.ConnectHandler(platformView);
        _ = StartAsync(platformView);
    }

    protected override void DisconnectHandler(CameraPreviewView platformView)
    {
        if (_session != null)
        {
            if (_session.Running)
                _session.StopRunning();
            _session.Dispose();
            _session = null;
        }

        _photoOutput = null;
        base.DisconnectHandler(platformView);
    }

    public void ReportFailure(Exception error)
    {
        _logger.LogError(error, "Camera capture error");
        SetHealth(CameraHealth.Unavailable, error.Message);
        _ = RebuildAsync("capture-error");
    }

    public async Task RebuildAsync(string trigger)
    {
        _logger.LogInformation("Camera rebuild trigger {Trigger}", trigger);
        var view = PlatformView;
        if (view == null)
            return;

        if (_session != null)
        {
            if (_session.Running)
                _session.StopRunning();
            _session.Dispose();
            _session = null;
        }

        _photoOutput = null;
        SetHealth(CameraHealth.Recovering, null);
        await StartAsync(view).ConfigureAwait(false);
    }

    public async Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken)
    {
        if (Health != CameraHealth.Live)
            throw new ClockingException("The camera is reconnecting. Hold the badge to the reader again in a few seconds.");

        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_photoOutput == null || _session is not { Running: true })
            throw new ClockingException(_error ?? "The camera is not ready.");

        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = AVCapturePhotoSettings.Create();
        _delegate = new JpegPhotoDelegate(pending);
        _photoOutput.CapturePhoto(settings, _delegate);
        using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        return await pending.Task.ConfigureAwait(false);
    }

    private async Task StartAsync(CameraPreviewView view)
    {
        try
        {
            if (!await EnsureAccessAsync().ConfigureAwait(false))
            {
                Fail("Allow camera access to save clock photos.");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() => OpenSession(view)).ConfigureAwait(false);
            _ready.TrySetResult();
            SetHealth(CameraHealth.Live, null);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void OpenSession(CameraPreviewView view)
    {
        var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video)
            ?? throw new ClockingException("This Mac has no camera for clock photos.");

        var input = new AVCaptureDeviceInput(device, out var error);
        if (error != null)
            throw new ClockingException(error.LocalizedDescription);

        var session = new AVCaptureSession();
        session.BeginConfiguration();
        // 720p is plenty for the circle and the identity photo, and far cheaper than the full-sensor photo preset.
        session.SessionPreset = session.CanSetSessionPreset(AVCaptureSession.Preset1280x720)
            ? AVCaptureSession.Preset1280x720
            : AVCaptureSession.PresetHigh;
        if (!session.CanAddInput(input))
            throw new ClockingException("The camera could not be opened.");

        session.AddInput(input);
        var output = new AVCapturePhotoOutput();
        if (!session.CanAddOutput(output))
            throw new ClockingException("The camera could not take photos.");

        session.AddOutput(output);
        session.CommitConfiguration();
        view.Attach(session);
        session.StartRunning();
        _session = session;
        _photoOutput = output;
    }

    private static async Task<bool> EnsureAccessAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (status == AVAuthorizationStatus.Authorized)
            return true;
        if (status != AVAuthorizationStatus.NotDetermined)
            return false;

        return await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video).ConfigureAwait(false);
    }

    private void Fail(string message)
    {
        _error = message;
        _ready.TrySetResult();
        SetHealth(CameraHealth.Unavailable, message);
    }

    private void SetHealth(CameraHealth health, string? unavailableMessage)
    {
        _health = health;
        var text = health switch
        {
            CameraHealth.Live => "Camera live",
            CameraHealth.Unavailable => string.IsNullOrWhiteSpace(unavailableMessage) ? "Camera unavailable — retrying" : unavailableMessage,
            _ => "Camera reconnecting…"
        };
        _logger.LogInformation("Camera health {Health}", health);
        VirtualView?.SetStatus(text);
        HealthChanged?.Invoke(health);
    }
}

public sealed class CameraPreviewView : UIView
{
    private AVCaptureVideoPreviewLayer? _preview;

    public void Attach(AVCaptureSession session)
    {
        _preview = new AVCaptureVideoPreviewLayer(session)
        {
            VideoGravity = AVLayerVideoGravity.ResizeAspectFill,
            Frame = Bounds
        };
        if (_preview.Connection is { } connection)
        {
            connection.AutomaticallyAdjustsVideoMirroring = false;
            if (connection.SupportsVideoMirroring)
                connection.VideoMirrored = true;
        }

        Layer.AddSublayer(_preview);
        ClipsToBounds = true;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_preview == null)
            return;

        _preview.Frame = Bounds;
        var side = Bounds.Width < Bounds.Height ? Bounds.Width : Bounds.Height;
        Layer.CornerRadius = side / 2;
    }
}

sealed class JpegPhotoDelegate : AVCapturePhotoCaptureDelegate
{
    private readonly TaskCompletionSource<byte[]?> _pending;

    public JpegPhotoDelegate(TaskCompletionSource<byte[]?> pending) => _pending = pending;

    /// <summary>A smaller copy when the photo is larger than <paramref name="maxEdge"/>. Null means the original is already small enough.</summary>
    private static UIImage? ScaleDown(UIImage? image, nfloat maxEdge)
    {
        if (image == null)
            return null;

        var longest = (nfloat)Math.Max(image.Size.Width, image.Size.Height);
        if (longest <= maxEdge || longest <= 0)
            return null;

        var scale = maxEdge / longest;
        var size = new CGSize(image.Size.Width * scale, image.Size.Height * scale);
        var format = new UIGraphicsImageRendererFormat { Opaque = false, Scale = 1 };
        var renderer = new UIGraphicsImageRenderer(size, format);
        return renderer.CreateImage(_ => image.Draw(new CGRect(0, 0, size.Width, size.Height)));
    }

    public override void DidFinishProcessingPhoto(AVCapturePhotoOutput output, AVCapturePhoto photo, NSError? error)
    {
        if (error != null)
        {
            _pending.TrySetException(new ClockingException(error.LocalizedDescription));
            return;
        }

        using var data = photo.FileDataRepresentation;
        if (data == null)
        {
            _pending.TrySetResult(null);
            return;
        }

        using var image = UIImage.LoadFromData(data);
        using var fitted = ScaleDown(image, 640);
        using var jpeg = (fitted ?? image)?.AsJPEG(0.5f);
        _pending.TrySetResult(jpeg?.ToArray());
    }
}
