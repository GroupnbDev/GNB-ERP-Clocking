using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Gnb.Clocking.App.Camera;
using Gnb.Clocking.App.Configuration;
using Gnb.Clocking.App.Diagnostics;
using Gnb.Clocking.Application.Clocking;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Stretch = Microsoft.UI.Xaml.Media.Stretch;
using Visibility = Microsoft.UI.Xaml.Visibility;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;

namespace Gnb.Clocking.App.Platforms.Windows;

// WinUI 3 never got CaptureElement. The preview is a MediaPlayerElement on the color frame source
// when the webcam allows a reader and a player to share it, otherwise the reader's frames painted
// onto an Image. The photo always comes from the newest reader frame, never a frozen player picture.
public sealed class WinClockCameraHandler : ViewHandler<ClockCameraView, WinGrid>, IClockCameraHandler
{
    public static IPropertyMapper<ClockCameraView, WinClockCameraHandler> PropertyMapper =
        new PropertyMapper<ClockCameraView, WinClockCameraHandler>(ViewMapper);

    private const uint MaxHeight = 480;
    private const uint PhotoMaxEdge = 640;
    private const double JpegQuality = 0.5;
    private const double MaxFrameRate = 30;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(1.0 / 15);

    private readonly ILogger<WinClockCameraHandler> _logger = KioskLog.Create<WinClockCameraHandler>();
    private readonly SemaphoreSlim _session = new(1, 1);
    private readonly object _rebuildGate = new();
    private readonly object _bitmapGate = new();

    private MediaPlayerElement? _playerView;
    private Microsoft.UI.Xaml.Controls.Image? _frameView;
    private CameraSession? _current;
    private SoftwareBitmap? _latestBitmap;
    private long _lastFrameAt;
    private long _lastPreviewAt;
    private long _lastCopyAt;
    private int _retryPending;
    private SoftwareBitmapSource? _shownPreview;
    private long _sessionStartedAt;
    private int _startAttempt;
    private int _closed;
    private int _skipSharedPreview;
    private Task? _rebuild;
    private DispatcherQueueTimer? _watchdog;
    private CameraStreamState _streamState = CameraStreamState.Streaming;
    private long _leftStreamingAt;
    private CameraHealth _health = CameraHealth.Starting;
    private string? _error;

    public WinClockCameraHandler() : base(PropertyMapper)
    {
    }

    public CameraHealth Health => _health;

    public event Action<CameraHealth>? HealthChanged;

    protected override WinGrid CreatePlatformView()
    {
        _playerView = new MediaPlayerElement
        {
            Stretch = Stretch.UniformToFill,
            AreTransportControlsEnabled = false,
            IsTabStop = false
        };
        _frameView = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Stretch.UniformToFill,
            IsTabStop = false,
            Visibility = Visibility.Collapsed
        };
        var grid = new WinGrid();
        grid.Children.Add(_playerView);
        grid.Children.Add(_frameView);
        return grid;
    }

    protected override void ConnectHandler(WinGrid platformView)
    {
        base.ConnectHandler(platformView);
        var queue = platformView.DispatcherQueue;
        _watchdog = queue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(1);
        _watchdog.Tick += (_, _) => OnWatchdog();
        _watchdog.Start();
        CameraPowerSignals.Attach(RebuildAsync, () => Health);
        _ = RebuildAsync("start");
    }

    protected override void DisconnectHandler(WinGrid platformView)
    {
        _closed = 1;
        _watchdog?.Stop();
        _ = DisposeSessionAsync();
        base.DisconnectHandler(platformView);
    }

    public void ReportFailure(Exception error)
    {
        _logger.LogError(error, "Camera capture error");
        _ = RebuildAsync("capture-error");
    }

    public Task RebuildAsync(string trigger)
    {
        lock (_rebuildGate)
        {
            if (_rebuild is { IsCompleted: false })
                return _rebuild;

            var task = RebuildCoreAsync(trigger);
            _rebuild = task;
            return task;
        }
    }

    public async Task<byte[]?> CaptureJpegAsync(CancellationToken cancellationToken)
    {
        if (Health != CameraHealth.Live)
            throw new ClockingException("The camera is reconnecting. Hold the badge to the reader again in a few seconds.");

        var age = FrameAgeMs();
        if (age > CameraSettingsAccess.MaxPhotoAgeMs)
        {
            _logger.LogWarning("Camera frame is {AgeMs} ms old; rebuilding", age);
            _ = RebuildAsync("capture-error");
            throw new ClockingException("The camera is reconnecting. Hold the badge to the reader again in a few seconds.");
        }

        await _session.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (Health != CameraHealth.Live)
                throw new ClockingException("The camera is reconnecting. Hold the badge to the reader again in a few seconds.");

            var encoded = await EncodeLatestAsync(cancellationToken).ConfigureAwait(true);
            if (encoded is { Length: > 0 })
                return encoded;

            if (_current?.Capture is MediaCapture fallback)
                return await CapturePhotoFallbackAsync(fallback, cancellationToken).ConfigureAwait(true);

            throw new ClockingException("The camera is reconnecting. Hold the badge to the reader again in a few seconds.");
        }
        finally
        {
            _session.Release();
        }
    }

    private async Task RebuildCoreAsync(string trigger)
    {
        if (_closed == 1)
            return;

        var started = Stopwatch.GetTimestamp();
        await _session.WaitAsync().ConfigureAwait(true);
        try
        {
            await RunOnUiAsync(() => SetHealth(trigger == "start" ? CameraHealth.Starting : CameraHealth.Recovering, null)).ConfigureAwait(false);
            _logger.LogInformation("Camera rebuild trigger {Trigger}", trigger);
            await DisposeSessionAsync().ConfigureAwait(true);
            var opened = await StartSessionAsync().ConfigureAwait(true);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (opened)
            {
                _logger.LogInformation("Camera rebuild finished trigger {Trigger} elapsed {ElapsedMs} outcome ok", trigger, (long)elapsed);
            }
            else
            {
                _logger.LogWarning("Camera rebuild finished trigger {Trigger} elapsed {ElapsedMs} outcome failed", trigger, (long)elapsed);
                ScheduleRetry();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera rebuild trigger {Trigger} threw", trigger);
            try
            {
                await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Camera unavailable — retrying")).ConfigureAwait(false);
            }
            catch (Exception healthEx)
            {
                _logger.LogWarning(healthEx, "Camera status update failed");
            }

            ScheduleRetry();
        }
        finally
        {
            _session.Release();
        }
    }

    private void ScheduleRetry()
    {
        if (_closed == 1 || Interlocked.Exchange(ref _retryPending, 1) == 1)
            return;

        var attempt = ++_startAttempt;
        var delay = CameraRecoveryPolicy.NextDelay(attempt);
        _logger.LogInformation("Camera retry {Attempt} in {DelaySeconds}s", attempt, delay.TotalSeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                Interlocked.Exchange(ref _retryPending, 0);
                if (_closed == 0)
                    await RebuildAsync("failed").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _retryPending, 0);
                _logger.LogError(ex, "Camera retry scheduling failed");
            }
        });
    }

    private async Task<bool> StartSessionAsync()
    {
        var view = _playerView;
        if (view == null)
            return false;

        var media = new MediaCapture();
        MediaFrameReader? reader = null;
        try
        {
            await media.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            }).AsTask().ConfigureAwait(true);

            var format = await UseLightPreviewAsync(media).ConfigureAwait(true);
            if (format != null)
                _logger.LogInformation("Camera preview format {Width}x{Height}", format.Width, format.Height);

            var source = FindColorFrameSource(media)
                ?? throw new ClockingException("No video preview or record stream found.");

            reader = await media.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(true);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            var readerStatus = await reader.StartAsync().AsTask().ConfigureAwait(true);
            if (readerStatus != MediaFrameReaderStartStatus.Success)
                throw new ClockingException($"The camera frame reader did not start ({readerStatus}).");

            MediaPlayer? player = null;
            MediaSource? mediaSource = null;
            var shared = false;
            // MediaPlayer and the preview control are bound to the window thread. Creating or
            // disposing them anywhere else closes the process right after "Camera reconnecting…".
            await RunOnUiAsync(() =>
            {
                if (_skipSharedPreview == 1)
                {
                    if (_frameView != null)
                        _frameView.Visibility = Visibility.Visible;
                    return;
                }

                try
                {
                    mediaSource = MediaSource.CreateFromMediaFrameSource(source);
                    player = new MediaPlayer
                    {
                        RealTimePlayback = true,
                        AutoPlay = false,
                        Source = mediaSource
                    };
                    view.SetMediaPlayer(player);
                    player.Play();
                    if (_frameView != null)
                        _frameView.Visibility = Visibility.Collapsed;
                    shared = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Camera frame reader and preview cannot share a source; painting frames instead");
                    _skipSharedPreview = 1;
                    try
                    {
                        view.SetMediaPlayer(null);
                    }
                    catch (Exception detachEx)
                    {
                        _logger.LogWarning(detachEx, "Camera preview detach threw");
                    }

                    try
                    {
                        player?.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        _logger.LogWarning(disposeEx, "Camera player dispose threw");
                    }

                    try
                    {
                        mediaSource?.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        _logger.LogWarning(disposeEx, "Camera media source dispose threw");
                    }

                    player = null;
                    mediaSource = null;
                    if (_frameView != null)
                        _frameView.Visibility = Visibility.Visible;
                }
            }).ConfigureAwait(false);

            media.Failed += OnMediaFailed;
            media.CameraStreamStateChanged += OnStreamStateChanged;
            _streamState = CameraStreamState.Streaming;
            _leftStreamingAt = 0;
            _lastFrameAt = 0;
            _sessionStartedAt = Stopwatch.GetTimestamp();
            _current = new CameraSession(media, source, mediaSource, player, reader, view);
            _logger.LogInformation("Camera frame reader sharing {Mode}", shared ? "shared" : "preview-from-reader");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Camera permission denied");
            ReleaseFailedStart(media, reader);
            await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Allow camera access to save clock photos.")).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera start failed");
            ReleaseFailedStart(media, reader);
            await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Camera unavailable — retrying")).ConfigureAwait(false);
            return false;
        }
    }

    private void ReleaseFailedStart(MediaCapture media, MediaFrameReader? reader)
    {
        try
        {
            if (reader != null)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
            }

            media.Failed -= OnMediaFailed;
            media.CameraStreamStateChanged -= OnStreamStateChanged;
            media.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera dispose after a failed start threw");
        }
    }

    private void OnMediaFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
    {
        // A session being torn down reports its own shutdown; only the live one counts.
        if (!ReferenceEquals(sender, _current?.Capture))
            return;

        _logger.LogError("MediaCapture failed {Code} {Message}", args.Code, args.Message);
        if (_current?.Player != null)
            _skipSharedPreview = 1;
        _ = RebuildAsync("failed");
    }

    private void OnStreamStateChanged(MediaCapture sender, object args)
    {
        if (!ReferenceEquals(sender, _current?.Capture))
            return;

        var state = sender.CameraStreamState;
        _streamState = state;
        _logger.LogInformation("Camera stream state {State}", state);
        if (state == CameraStreamState.Shutdown)
        {
            if (_current?.Player != null)
                _skipSharedPreview = 1;
            _ = RebuildAsync("stream-state");
            return;
        }

        if (state == CameraStreamState.Streaming)
            _leftStreamingAt = 0;
        else if (_leftStreamingAt == 0)
            _leftStreamingAt = Stopwatch.GetTimestamp();
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        // A reader still draining while its session is disposed must not mark the camera live again.
        if (!ReferenceEquals(sender, _current?.Reader))
            return;

        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null)
                return;

            var now = Stopwatch.GetTimestamp();
            _lastFrameAt = now;
            if (Health != CameraHealth.Live)
            {
                _startAttempt = 0;
                SetHealth(CameraHealth.Live, null);
            }

            // Copying every 30 fps frame churns ~100 MB/s on a 720p stream. The photo only has to be
            // newer than CameraMaxPhotoAgeMs, and the painted preview runs at 15 fps.
            if (_lastCopyAt != 0 && Stopwatch.GetElapsedTime(_lastCopyAt, now) < PreviewInterval)
                return;

            _lastCopyAt = now;
            var copy = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            SoftwareBitmap? previous;
            lock (_bitmapGate)
            {
                previous = _latestBitmap;
                _latestBitmap = copy;
            }

            previous?.Dispose();
            if (_frameView?.Visibility == Visibility.Visible)
                PaintFrame();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera frame was dropped");
        }
    }

    private void PaintFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewAt != 0 && Stopwatch.GetElapsedTime(_lastPreviewAt, now) < PreviewInterval)
            return;

        _lastPreviewAt = now;
        var queue = PlatformView?.DispatcherQueue;
        if (queue == null)
            return;

        SoftwareBitmap? preview;
        lock (_bitmapGate)
            preview = _latestBitmap == null ? null : SoftwareBitmap.Copy(_latestBitmap);

        if (preview == null)
            return;

        queue.TryEnqueue(async () =>
        {
            try
            {
                if (_frameView == null)
                    return;

                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(preview);
                _frameView.Source = source;
                // Each source holds native pixels; 15 a second for days adds up if they wait for the GC.
                var shown = _shownPreview;
                _shownPreview = source;
                shown?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Camera preview paint failed");
            }
            finally
            {
                preview.Dispose();
            }
        });
    }

    private void OnWatchdog()
    {
        if (_closed == 1 || _current == null)
            return;

        var stallMs = CameraSettingsAccess.StallMilliseconds;
        var frameAge = _lastFrameAt == 0 ? long.MaxValue : FrameAgeMs();
        if (CameraWatchdogPolicy.Evaluate(Health, frameAge, stallMs) == CameraWatchdogAction.Stall)
        {
            _logger.LogWarning("Camera stalled after {AgeMs} ms", frameAge);
            SetHealth(CameraHealth.Stalled, null);
            _ = RebuildAsync("stall");
            return;
        }

        if (_leftStreamingAt != 0
            && _streamState != CameraStreamState.Streaming
            && _streamState != CameraStreamState.Shutdown
            && Stopwatch.GetElapsedTime(_leftStreamingAt).TotalMilliseconds >= stallMs)
        {
            _logger.LogWarning("Camera left streaming for {StallMs} ms ({State})", stallMs, _streamState);
            _ = RebuildAsync("stall");
            return;
        }

        if (Health is CameraHealth.Starting or CameraHealth.Recovering
            && _sessionStartedAt != 0
            && Stopwatch.GetElapsedTime(_sessionStartedAt).TotalMilliseconds > stallMs
            && _lastFrameAt == 0)
        {
            _logger.LogWarning("Camera produced no frames within {StallMs} ms", stallMs);
            SetHealth(CameraHealth.Unavailable, "No picture from the camera — retrying");
            ScheduleRetry();
        }
    }

    private long FrameAgeMs()
    {
        if (_lastFrameAt == 0)
            return long.MaxValue;
        return (long)Stopwatch.GetElapsedTime(_lastFrameAt).TotalMilliseconds;
    }

    private async Task<byte[]?> EncodeLatestAsync(CancellationToken cancellationToken)
    {
        SoftwareBitmap? copy;
        lock (_bitmapGate)
            copy = _latestBitmap == null ? null : SoftwareBitmap.Copy(_latestBitmap);

        if (copy == null)
            return null;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var properties = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single) }
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties).AsTask(cancellationToken).ConfigureAwait(true);
            encoder.SetSoftwareBitmap(copy);
            var longEdge = Math.Max(copy.PixelWidth, copy.PixelHeight);
            if (longEdge > PhotoMaxEdge)
            {
                var scale = PhotoMaxEdge / (double)longEdge;
                encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(copy.PixelWidth * scale));
                encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(copy.PixelHeight * scale));
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            }

            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(true);
            stream.Seek(0);
            var length = (int)Math.Min(stream.Size, int.MaxValue);
            var bytes = new byte[length];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(true);
            return bytes;
        }
        finally
        {
            copy.Dispose();
        }
    }

    private async Task<byte[]?> CapturePhotoFallbackAsync(MediaCapture media, CancellationToken cancellationToken)
    {
        var encoding = ImageEncodingProperties.CreateJpeg();
        using var stream = new InMemoryRandomAccessStream();
        await media.CapturePhotoToStreamAsync(encoding, stream).AsTask(cancellationToken).ConfigureAwait(true);
        stream.Seek(0);
        var length = (int)Math.Min(stream.Size, int.MaxValue);
        var bytes = new byte[length];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(cancellationToken).ConfigureAwait(true);
        return bytes;
    }

    private async Task DisposeSessionAsync()
    {
        var session = _current;
        _current = null;
        if (session == null)
            return;

        try
        {
            session.Reader.FrameArrived -= OnFrameArrived;
            session.Capture.Failed -= OnMediaFailed;
            session.Capture.CameraStreamStateChanged -= OnStreamStateChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera event detach threw");
        }

        await session.DisposeAsync(_logger).ConfigureAwait(true);
        lock (_bitmapGate)
        {
            _latestBitmap?.Dispose();
            _latestBitmap = null;
            _lastFrameAt = 0;
            _lastCopyAt = 0;
        }
    }

    private Task RunOnUiAsync(Action action)
    {
        var queue = _playerView?.DispatcherQueue ?? PlatformView?.DispatcherQueue;
        if (queue == null || queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
            done.TrySetException(new InvalidOperationException("The camera preview could not be updated on the window thread."));

        return done.Task;
    }

    private void SetHealth(CameraHealth health, string? unavailableMessage)
    {
        _health = health;
        if (health == CameraHealth.Unavailable)
            _error = unavailableMessage;
        var text = health switch
        {
            CameraHealth.Live => "Camera live",
            CameraHealth.Unavailable => string.IsNullOrWhiteSpace(_error) ? "Camera unavailable — retrying" : _error!,
            _ => "Camera reconnecting…"
        };
        _logger.LogInformation("Camera health {Health}", health);
        VirtualView?.SetStatus(text);
        var changed = HealthChanged;
        var queue = PlatformView?.DispatcherQueue;
        if (queue != null)
            queue.TryEnqueue(() => changed?.Invoke(health));
        else
            changed?.Invoke(health);
    }

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
            return null;
        }
    }

    private static double FrameRate(VideoEncodingProperties format) =>
        format.FrameRate.Denominator == 0 ? 0 : (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

    private sealed class CameraSession
    {
        private readonly MediaPlayerElement _view;

        public CameraSession(
            MediaCapture capture,
            MediaFrameSource source,
            MediaSource? mediaSource,
            MediaPlayer? player,
            MediaFrameReader reader,
            MediaPlayerElement view)
        {
            Capture = capture;
            Source = source;
            MediaSource = mediaSource;
            Player = player;
            Reader = reader;
            _view = view;
        }

        public MediaCapture Capture { get; }
        public MediaFrameSource Source { get; }
        public MediaSource? MediaSource { get; }
        public MediaPlayer? Player { get; }
        public MediaFrameReader Reader { get; }

        public async Task DisposeAsync(ILogger logger)
        {
            var queue = _view.DispatcherQueue;
            if (queue != null)
            {
                var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!queue.TryEnqueue(() =>
                    {
                        try
                        {
                            _view.SetMediaPlayer(null);
                            try
                            {
                                Player?.Pause();
                                Player?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Camera player dispose threw");
                            }

                            try
                            {
                                MediaSource?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Camera media source dispose threw");
                            }
                        }
                        finally
                        {
                            detached.TrySetResult();
                        }
                    }))
                    detached.TrySetResult();
                await detached.Task.ConfigureAwait(false);
            }

            try
            {
                await Reader.StopAsync().AsTask().ConfigureAwait(false);
                Reader.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Camera frame reader dispose threw");
            }

            var capture = Capture;
            var disposed = Task.Run(() =>
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Camera MediaCapture dispose threw");
                }
            });
            var finished = await Task.WhenAny(disposed, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished != disposed)
                logger.LogWarning("MediaCapture dispose timed out");
        }
    }
}
