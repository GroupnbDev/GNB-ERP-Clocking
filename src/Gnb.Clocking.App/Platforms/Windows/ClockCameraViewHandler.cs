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

// WinUI 3 never got CaptureElement. The preview is the frame reader's pictures painted onto an
// Image. A MediaPlayer on that same source is what older laptop drivers use to close the process
// after about an hour. The photo always comes from the newest reader frame.
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
    // Sharing the webcam with a MediaPlayer as well as the frame reader is what older drivers
    // kill after about an hour, and that takes the whole kiosk with it. Paint the reader frames.
    private int _skipSharedPreview = 1;
    private int _paintQueued;
    private SoftwareBitmap? _pendingPreview;
    private long _recycledSessionAt;
    private Task? _rebuild;
    private Task? _captureDispose;
    private DispatcherQueue? _uiQueue;
    private DispatcherQueueTimer? _watchdog;
    private int _paintPreview;
    private int _loggedFrameFormat;
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
        _uiQueue = queue;
        _watchdog = queue.CreateTimer();
        _watchdog.Interval = TimeSpan.FromSeconds(1);
        _watchdog.Tick += (_, _) => OnWatchdog();
        _watchdog.Start();
        KioskStayAwake.Hold();
        CameraPowerSignals.Attach(RebuildAsync, () => Health);
        _ = RebuildAsync("start");
    }

    protected override void DisconnectHandler(WinGrid platformView)
    {
        _closed = 1;
        Volatile.Write(ref _paintPreview, 0);
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
        await _session.WaitAsync().ConfigureAwait(false);
        try
        {
            // Reset is tapped on the window thread. A finished wait stays there, and MediaCapture
            // teardown on that thread is what older laptops report as "Not Responding", then close.
            await LeaveWindowThreadAsync().ConfigureAwait(false);
            await RunOnUiAsync(() => SetHealth(trigger == "start" ? CameraHealth.Starting : CameraHealth.Recovering, null)).ConfigureAwait(false);
            _logger.LogInformation("Camera rebuild trigger {Trigger}", trigger);
            var released = await DisposeSessionAsync().ConfigureAwait(false);
            if (!released)
            {
                _logger.LogWarning("Camera rebuild trigger {Trigger} left the previous camera open", trigger);
                await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Camera unavailable — retrying")).ConfigureAwait(false);
                ScheduleRetry();
                return;
            }

            var opened = await StartSessionAsync().ConfigureAwait(false);
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
        await LeaveWindowThreadAsync().ConfigureAwait(false);
        if (_captureDispose is { IsCompleted: false })
        {
            _logger.LogWarning("Camera is still closing from the last reset");
            return false;
        }

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
                    SetReaderPreview(true);
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
                    SetReaderPreview(false);
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
                    SetReaderPreview(true);
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
            await ReleaseFailedStartAsync(media, reader).ConfigureAwait(false);
            await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Allow camera access to save clock photos.")).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera start failed");
            await ReleaseFailedStartAsync(media, reader).ConfigureAwait(false);
            await RunOnUiAsync(() => SetHealth(CameraHealth.Unavailable, "Camera unavailable — retrying")).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReleaseFailedStartAsync(MediaCapture media, MediaFrameReader? reader)
    {
        await LeaveWindowThreadAsync().ConfigureAwait(false);
        try
        {
            if (reader != null)
            {
                reader.FrameArrived -= OnFrameArrived;
                var stop = reader.StopAsync().AsTask();
                if (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false) != stop)
                {
                    _logger.LogWarning("Camera frame reader stop timed out");
                    _captureDispose = stop.ContinueWith(
                        _ => ReleaseFailedObjects(reader, media),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                    return;
                }

                await stop.ConfigureAwait(false);
                reader.Dispose();
            }

            media.Failed -= OnMediaFailed;
            media.CameraStreamStateChanged -= OnStreamStateChanged;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera dispose after a failed start threw");
        }

        var captureDispose = Task.Run(() => ReleaseFailedObjects(null, media));
        _captureDispose = captureDispose;
        if (await Task.WhenAny(captureDispose, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != captureDispose)
            _logger.LogWarning("MediaCapture dispose timed out");
    }

    private void ReleaseFailedObjects(MediaFrameReader? reader, MediaCapture media)
    {
        if (reader != null)
        {
            try
            {
                reader.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Camera frame reader dispose threw");
            }
        }

        try
        {
            media.Failed -= OnMediaFailed;
            media.CameraStreamStateChanged -= OnStreamStateChanged;
            media.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera MediaCapture dispose threw");
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
            if (Interlocked.Exchange(ref _loggedFrameFormat, 1) == 0)
                _logger.LogInformation(
                    "Camera frame {Format} alpha {Alpha} {Width}x{Height}",
                    bitmap.BitmapPixelFormat,
                    bitmap.BitmapAlphaMode,
                    bitmap.PixelWidth,
                    bitmap.PixelHeight);

            var copy = CopyForDisplay(bitmap);
            SoftwareBitmap? previous;
            lock (_bitmapGate)
            {
                previous = _latestBitmap;
                _latestBitmap = copy;
            }

            previous?.Dispose();
            if (Volatile.Read(ref _paintPreview) == 1)
                QueuePreview();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Camera frame was dropped");
        }
    }

    /// <summary>
    /// Frame callbacks are not on the window thread. <see cref="UIElement.Visibility"/> throws
    /// RPC_E_WRONG_THREAD there, which was dropping every picture and leaving the well black.
    /// </summary>
    private void SetReaderPreview(bool paint)
    {
        Volatile.Write(ref _paintPreview, paint ? 1 : 0);
        if (_frameView != null)
            _frameView.Visibility = paint ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A straight-alpha webcam frame often has alpha 0. Converting that to premultiplied
    /// multiplies the color by 0, so the preview and the saved photo are black.
    /// </summary>
    private static SoftwareBitmap CopyForDisplay(SoftwareBitmap bitmap)
    {
        if (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
            && bitmap.BitmapAlphaMode is BitmapAlphaMode.Premultiplied or BitmapAlphaMode.Ignore)
            return SoftwareBitmap.Copy(bitmap);

        return SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }

    private void QueuePreview()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewAt != 0 && Stopwatch.GetElapsedTime(_lastPreviewAt, now) < PreviewInterval)
            return;

        var queue = _uiQueue;
        if (queue == null)
            return;

        lock (_bitmapGate)
        {
            var copy = _latestBitmap == null ? null : SoftwareBitmap.Copy(_latestBitmap);
            if (copy == null)
                return;

            var dropped = _pendingPreview;
            _pendingPreview = copy;
            dropped?.Dispose();
        }

        _lastPreviewAt = now;
        if (Interlocked.CompareExchange(ref _paintQueued, 1, 0) != 0)
            return;

        if (!queue.TryEnqueue(PaintQueuedFrame))
            Interlocked.Exchange(ref _paintQueued, 0);
    }

    private async void PaintQueuedFrame()
    {
        SoftwareBitmap? bitmap = null;
        try
        {
            lock (_bitmapGate)
            {
                bitmap = _pendingPreview;
                _pendingPreview = null;
            }

            if (bitmap == null || _frameView == null)
                return;

            SoftwareBitmapSource? source = new SoftwareBitmapSource();
            try
            {
                await source.SetBitmapAsync(bitmap);
                _frameView.Source = source;
                var shown = _shownPreview;
                _shownPreview = source;
                source = null;
                shown?.Dispose();
            }
            finally
            {
                source?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Camera preview paint failed");
        }
        finally
        {
            bitmap?.Dispose();
            Interlocked.Exchange(ref _paintQueued, 0);
            bool more;
            lock (_bitmapGate)
                more = _pendingPreview != null;

            if (more && _closed != 1 && Interlocked.CompareExchange(ref _paintQueued, 1, 0) == 0)
            {
                var queue = _uiQueue;
                if (queue == null || !queue.TryEnqueue(PaintQueuedFrame))
                    Interlocked.Exchange(ref _paintQueued, 0);
            }
        }
    }

    private void OnWatchdog()
    {
        KioskStayAwake.Hold();
        if (_closed == 1 || _current == null)
            return;

        if (Health == CameraHealth.Live
            && _sessionStartedAt != 0
            && _recycledSessionAt != _sessionStartedAt
            && CameraRecoveryPolicy.IsSessionRecycleDue(
                Stopwatch.GetElapsedTime(_sessionStartedAt),
                busy: _session.CurrentCount == 0))
        {
            _recycledSessionAt = _sessionStartedAt;
            _logger.LogInformation("Recycling the camera session so the kiosk can stay open");
            _ = RebuildAsync("session-age");
            return;
        }

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

    private async Task<bool> DisposeSessionAsync()
    {
        await LeaveWindowThreadAsync().ConfigureAwait(false);
        if (_captureDispose is { IsCompleted: false })
            return false;

        var session = _current;
        if (session == null)
            return true;

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

        var released = await session.DisposeAsync(_logger, task => _captureDispose = task).ConfigureAwait(false);
        if (!released)
        {
            if (!session.ReaderStopped && !session.StopInFlight)
            {
                session.Reader.FrameArrived += OnFrameArrived;
                session.Capture.Failed += OnMediaFailed;
                session.Capture.CameraStreamStateChanged += OnStreamStateChanged;
            }

            return false;
        }

        _current = null;
        lock (_bitmapGate)
        {
            _latestBitmap?.Dispose();
            _latestBitmap = null;
            _pendingPreview?.Dispose();
            _pendingPreview = null;
            _lastFrameAt = 0;
            _lastCopyAt = 0;
        }

        return _captureDispose is not { IsCompleted: false };
    }

    /// <summary>
    /// A completed await stays on the caller. Camera reset starts on the window thread, so this
    /// hops to the thread pool before any MediaCapture call.
    /// </summary>
    private static async Task LeaveWindowThreadAsync()
    {
        if (DispatcherQueue.GetForCurrentThread() == null)
            return;

        await Task.Run(() => { }).ConfigureAwait(false);
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
        private int _readerStopped;
        private int _stopInFlight;
        private int _playerReleased;
        private int _playerReleaseStarted;
        private int _captureReleased;
        private Task? _captureDispose;

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

        public bool ReaderStopped => Volatile.Read(ref _readerStopped) == 1;

        public bool StopInFlight => Volatile.Read(ref _stopInFlight) == 1;

        public async Task<bool> DisposeAsync(ILogger logger, Action<Task> trackCaptureDispose)
        {
            if (!await StopReaderAsync(logger).ConfigureAwait(false))
                return false;

            if (!await ReleasePlayerAsync(logger).ConfigureAwait(false))
                return false;

            DisposeReader(logger);
            return await ReleaseCaptureAsync(logger, trackCaptureDispose).ConfigureAwait(false);
        }

        private async Task<bool> StopReaderAsync(ILogger logger)
        {
            if (_readerStopped == 1)
                return true;

            if (Interlocked.Exchange(ref _stopInFlight, 1) == 1)
                return false;

            var stop = Reader.StopAsync().AsTask();
            var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
            if (finished != stop)
            {
                logger.LogWarning("Camera frame reader stop timed out");
                _ = stop.ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                            logger.LogWarning(task.Exception, "Camera frame reader stop threw after timeout");
                        else
                            Volatile.Write(ref _readerStopped, 1);
                        Interlocked.Exchange(ref _stopInFlight, 0);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                return false;
            }

            try
            {
                await stop.ConfigureAwait(false);
                Volatile.Write(ref _readerStopped, 1);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Camera frame reader stop threw");
                Volatile.Write(ref _readerStopped, 1);
                return true;
            }
            finally
            {
                Interlocked.Exchange(ref _stopInFlight, 0);
            }
        }

        private int _readerDisposed;

        private void DisposeReader(ILogger logger)
        {
            if (Interlocked.Exchange(ref _readerDisposed, 1) == 1)
                return;

            try
            {
                Reader.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Camera frame reader dispose threw");
            }
        }

        private async Task<bool> ReleasePlayerAsync(ILogger logger)
        {
            if (_playerReleased == 1)
                return true;

            if (Interlocked.Exchange(ref _playerReleaseStarted, 1) == 1)
                return false;

            var queue = _view.DispatcherQueue;
            if (queue == null)
            {
                _playerReleased = 1;
                return true;
            }

            var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryEnqueue(() =>
                {
                    try
                    {
                        _view.SetMediaPlayer(null);
                        if (Player != null)
                        {
                            Player.Pause();
                            Player.Source = null;
                            Player.Dispose();
                        }

                        MediaSource?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Camera player dispose threw");
                    }
                    finally
                    {
                        _playerReleased = 1;
                        detached.TrySetResult();
                    }
                }))
            {
                _playerReleased = 1;
                return true;
            }

            var finished = await Task.WhenAny(detached.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (finished == detached.Task)
                return true;

            logger.LogWarning("Camera player detach timed out");
            return false;
        }

        private async Task<bool> ReleaseCaptureAsync(ILogger logger, Action<Task> trackCaptureDispose)
        {
            if (_captureReleased == 1)
                return true;

            if (_captureDispose is { IsCompleted: false })
                return false;

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
            trackCaptureDispose(disposed);
            _captureDispose = disposed;
            var finished = await Task.WhenAny(disposed, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished != disposed)
            {
                logger.LogWarning("MediaCapture dispose timed out");
                _ = disposed.ContinueWith(
                    task =>
                    {
                        _captureReleased = 1;
                        if (task.IsFaulted)
                            logger.LogWarning(task.Exception, "Camera MediaCapture dispose threw after timeout");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
                return false;
            }

            _captureReleased = 1;
            return true;
        }
    }
}
