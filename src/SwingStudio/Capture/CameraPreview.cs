using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCvSharp;

namespace SwingStudio.Capture;

public sealed class CameraPreview : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<BitmapSource, int, int, double> _onFrame;
    private readonly Action<string> _onError;
    private readonly string _label;
    private Thread? _thread;
    private int _generation;
    private volatile bool _stop;
    private volatile bool _showSettings;
    private Action<IReadOnlyDictionary<string, double>>? _onSettingsSaved;
    private Action<Mat>? _onCaptured;
    private double _measuredFps;
    private VideoCapture? _dialogCapture;
    private Mat? _dialogFrame;
    private int _dialogGeneration;
    private TimerProc? _dialogTimer;
    private nuint _dialogTimerId;
    private bool _dialogErrorLogged;

    public CameraPreview(string label, Dispatcher dispatcher, Action<BitmapSource, int, int, double> onFrame, Action<string> onError)
    {
        _label = label;
        _dispatcher = dispatcher;
        _onFrame = onFrame;
        _onError = onError;
    }

    public void Start(int index, int width, int height, double framesPerSecond, string fourCc, string? devicePath, IReadOnlyDictionary<string, double>? controls, Action<Mat>? onCaptured = null)
    {
        Stop();
        _stop = false;
        _showSettings = false;
        _onCaptured = onCaptured;
        var generation = Interlocked.Increment(ref _generation);
        _thread = new Thread(() => CaptureLoop(index, width, height, framesPerSecond, fourCc, devicePath, controls, generation))
        {
            IsBackground = true,
            Name = "CameraPreview"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool RequestSettings(Action<IReadOnlyDictionary<string, double>> onSaved)
    {
        if (_thread is not { IsAlive: true })
        {
            return false;
        }

        _onSettingsSaved = onSaved;
        _showSettings = true;
        return true;
    }

    public void Stop()
    {
        _stop = true;
        Interlocked.Increment(ref _generation);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void CaptureLoop(int index, int width, int height, double framesPerSecond, string fourCc, string? devicePath, IReadOnlyDictionary<string, double>? controls, int generation)
    {
        try
        {
            ReadFrames(index, width, height, framesPerSecond, fourCc, devicePath, controls, generation);
        }
        catch (Exception ex)
        {
            Log.Error($"{_label} capture stopped with an error.", ex);
            ReportError(generation, ex.Message);
        }
    }

    private void ReadFrames(int index, int width, int height, double framesPerSecond, string fourCc, string? devicePath, IReadOnlyDictionary<string, double>? controls, int generation)
    {
        using var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            Log.Error($"{_label} did not open (DirectShow index {index}).");
            ReportError(generation, "The camera did not open.");
            return;
        }

        // DirectShow drops back to the default FourCC whenever width, height, or fps is set, so FourCC goes last.
        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);
        capture.Set(VideoCaptureProperties.Fps, framesPerSecond);
        capture.Set(VideoCaptureProperties.FourCC, FourCC.FromString(fourCc));
        capture.Set(VideoCaptureProperties.BufferSize, 1);
        capture.Set(VideoCaptureProperties.ConvertRgb, 1);
        if (controls is not null)
        {
            CameraControlStore.Apply(capture, devicePath, controls);
        }

        using var frame = new Mat();
        var appliedAfterFrame = false;
        var clock = Stopwatch.StartNew();
        var paintClock = Stopwatch.StartNew();
        var frames = 0;
        var measuredFps = 0d;
        var firstFrameLogged = false;
        var loggedFps = 0d;
        var fpsLogClock = Stopwatch.StartNew();

        while (!_stop && generation == Volatile.Read(ref _generation))
        {
            if (_showSettings)
            {
                _showSettings = false;
                ShowSettingsDialog(capture, generation);
                WaitForSettingsDialog(capture, frame, generation);
                var saved = CameraControlStore.Read(capture, devicePath);
                var callback = _onSettingsSaved;
                appliedAfterFrame = true;
                _dispatcher.BeginInvoke(() =>
                {
                    if (generation == Volatile.Read(ref _generation))
                    {
                        callback?.Invoke(saved);
                    }
                });
            }

            if (!capture.Read(frame) || frame.Empty())
            {
                continue;
            }

            _onCaptured?.Invoke(frame);

            if (!firstFrameLogged)
            {
                firstFrameLogged = true;
                Log.Info($"{_label} first frame {frame.Width}x{frame.Height}, {frame.Channels()} channel(s); driver reports {capture.Get(VideoCaptureProperties.Fps):0.##} fps, FourCC {FourCcText(capture.Get(VideoCaptureProperties.FourCC))}.");
            }

            if (!appliedAfterFrame && controls is not null)
            {
                CameraControlStore.Apply(capture, devicePath, controls);
                appliedAfterFrame = true;
            }

            frames++;
            if (clock.ElapsedMilliseconds >= 1000)
            {
                measuredFps = frames * 1000d / clock.ElapsedMilliseconds;
                _measuredFps = measuredFps;
                frames = 0;
                clock.Restart();
                if (loggedFps <= 0 || (Math.Abs(measuredFps - loggedFps) > loggedFps * 0.25 && fpsLogClock.Elapsed > TimeSpan.FromSeconds(10)))
                {
                    Log.Info($"{_label} measured {measuredFps:0.0} fps (requested {framesPerSecond:0.##}).");
                    loggedFps = measuredFps;
                    fpsLogClock.Restart();
                }
            }

            if (paintClock.ElapsedMilliseconds < 33)
            {
                continue;
            }

            paintClock.Restart();
            var bitmap = CopyFrame(frame);
            var frameWidth = frame.Width;
            var frameHeight = frame.Height;
            var fps = measuredFps;
            _dispatcher.BeginInvoke(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _onFrame(bitmap, frameWidth, frameHeight, fps);
                }
            });
        }
    }

    private void PublishFrame(VideoCapture? capture, Mat? frame, int generation)
    {
        if (capture is null || frame is null || _stop || generation != Volatile.Read(ref _generation))
        {
            return;
        }

        try
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                return;
            }

            _onCaptured?.Invoke(frame);
            var bitmap = CopyFrame(frame);
            var width = frame.Width;
            var height = frame.Height;
            var fps = _measuredFps;
            _dispatcher.BeginInvoke(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    _onFrame(bitmap, width, height, fps);
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static BitmapSource CopyFrame(Mat frame, bool enhance = true)
    {
        if (enhance)
        {
            using var look = FrameLook.ApplyGray(frame);
            return ToBitmap(look, PixelFormats.Gray8);
        }

        var format = frame.Channels() switch
        {
            1 => PixelFormats.Gray8,
            4 => PixelFormats.Bgra32,
            _ => PixelFormats.Bgr24
        };
        return ToBitmap(frame, format);
    }

    private static BitmapSource ToBitmap(Mat frame, PixelFormat format)
    {
        var stride = (int)frame.Step();
        var bytes = new byte[stride * frame.Height];
        Marshal.Copy(frame.Data, bytes, 0, bytes.Length);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, format, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void ShowSettingsDialog(VideoCapture capture, int generation)
    {
        _dialogCapture = capture;
        _dialogFrame = new Mat();
        _dialogGeneration = generation;
        _dialogTimer = OnDialogTimer;
        _dialogErrorLogged = false;
        _dialogTimerId = SetTimer(IntPtr.Zero, 0, 33, _dialogTimer);
        try
        {
            capture.Set(VideoCaptureProperties.Settings, 1);
        }
        finally
        {
            if (_dialogTimerId != 0)
            {
                KillTimer(IntPtr.Zero, _dialogTimerId);
                _dialogTimerId = 0;
            }

            _dialogFrame.Dispose();
            _dialogFrame = null;
            _dialogCapture = null;
            _dialogTimer = null;
        }
    }

    private void OnDialogTimer(IntPtr hwnd, uint msg, nuint idEvent, uint time)
    {
        try
        {
            PublishFrame(_dialogCapture, _dialogFrame, _dialogGeneration);
        }
        catch (Exception ex)
        {
            if (!_dialogErrorLogged)
            {
                _dialogErrorLogged = true;
                Log.Warn($"{_label} frame update failed while the settings dialog was open.", ex);
            }
        }
    }

    private static string FourCcText(double value)
    {
        var code = (int)value;
        if (code <= 0)
        {
            return "unknown";
        }

        var chars = new[] { (char)(code & 0xFF), (char)((code >> 8) & 0xFF), (char)((code >> 16) & 0xFF), (char)((code >> 24) & 0xFF) };
        return chars.All(c => c >= 32 && c < 127) ? new string(chars) : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void WaitForSettingsDialog(VideoCapture capture, Mat frame, int generation)
    {
        var started = Stopwatch.StartNew();
        while (!_stop && started.Elapsed < TimeSpan.FromMilliseconds(600))
        {
            if (SettingsDialogOpen())
            {
                while (!_stop && SettingsDialogOpen())
                {
                    PublishFrame(capture, frame, generation);
                    Thread.Sleep(33);
                }

                return;
            }

            Thread.Sleep(50);
        }

        if (_stop)
        {
            CloseSettingsDialogs();
        }
    }

    private static bool SettingsDialogOpen()
    {
        var open = false;
        var processId = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId != processId || !IsWindowVisible(hwnd))
            {
                return true;
            }

            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (title.ToString().Contains("Properties", StringComparison.OrdinalIgnoreCase))
            {
                open = true;
            }

            return true;
        }, IntPtr.Zero);
        return open;
    }

    private static void CloseSettingsDialogs()
    {
        var processId = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.ToString().Contains("Properties", StringComparison.OrdinalIgnoreCase))
                {
                    PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                }
            }

            return true;
        }, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void TimerProc(IntPtr hwnd, uint msg, nuint idEvent, uint time);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern nuint SetTimer(IntPtr hwnd, nuint eventId, uint elapsed, TimerProc callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, nuint eventId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private void ReportError(int generation, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _onError(message);
            }
        });
    }
}
