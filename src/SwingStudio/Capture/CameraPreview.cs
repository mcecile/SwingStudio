using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private Thread? _thread;
    private int _generation;
    private volatile bool _stop;

    public CameraPreview(Dispatcher dispatcher, Action<BitmapSource, int, int, double> onFrame, Action<string> onError)
    {
        _dispatcher = dispatcher;
        _onFrame = onFrame;
        _onError = onError;
    }

    public void Start(int index, int width, int height, double framesPerSecond, string fourCc)
    {
        Stop();
        _stop = false;
        var generation = Interlocked.Increment(ref _generation);
        _thread = new Thread(() => CaptureLoop(index, width, height, framesPerSecond, fourCc, generation))
        {
            IsBackground = true,
            Name = "CameraPreview"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        Interlocked.Increment(ref _generation);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void CaptureLoop(int index, int width, int height, double framesPerSecond, string fourCc, int generation)
    {
        try
        {
            ReadFrames(index, width, height, framesPerSecond, fourCc, generation);
        }
        catch (Exception ex)
        {
            ReportError(generation, ex.Message);
        }
    }

    private void ReadFrames(int index, int width, int height, double framesPerSecond, string fourCc, int generation)
    {
        using var capture = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            ReportError(generation, "The camera did not open.");
            return;
        }

        capture.Set(VideoCaptureProperties.FourCC, FourCC.FromString(fourCc));
        capture.Set(VideoCaptureProperties.FrameWidth, width);
        capture.Set(VideoCaptureProperties.FrameHeight, height);
        capture.Set(VideoCaptureProperties.Fps, framesPerSecond);
        capture.Set(VideoCaptureProperties.BufferSize, 1);
        capture.Set(VideoCaptureProperties.ConvertRgb, 1);

        using var frame = new Mat();
        var clock = Stopwatch.StartNew();
        var paintClock = Stopwatch.StartNew();
        var frames = 0;
        var measuredFps = 0d;

        while (!_stop && generation == Volatile.Read(ref _generation))
        {
            if (!capture.Read(frame) || frame.Empty())
            {
                continue;
            }

            frames++;
            if (clock.ElapsedMilliseconds >= 1000)
            {
                measuredFps = frames * 1000d / clock.ElapsedMilliseconds;
                frames = 0;
                clock.Restart();
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

    private static BitmapSource CopyFrame(Mat frame)
    {
        var format = frame.Channels() switch
        {
            1 => PixelFormats.Gray8,
            4 => PixelFormats.Bgra32,
            _ => PixelFormats.Bgr24
        };
        var stride = (int)frame.Step();
        var bytes = new byte[stride * frame.Height];
        Marshal.Copy(frame.Data, bytes, 0, bytes.Length);
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, format, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }

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
