using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;
using SwingStudio.Session;
using SwingStudio.Settings;

namespace SwingStudio;

public partial class CalibrationWindow : Window
{
    private const int SwingCount = 3;
    private const double RoomSeconds = 5;
    private const double BeforeSeconds = 1.0;
    private const double AfterSeconds = 1.5;
    private const double KeepSeconds = 3;
    private static readonly Brush RoomBrush = Frozen(Color.FromRgb(0x80, 0x80, 0x80));
    private static readonly Brush ThresholdBrush = Frozen(Color.FromRgb(0xFF, 0xE0, 0x8A));
    private static readonly Brush TraceBrush = Frozen(Color.FromRgb(0x6F, 0xA8, 0xDC));
    private static readonly Brush DividerBrush = Frozen(Color.FromRgb(0x33, 0x33, 0x33));

    private enum Step { Ready, Room, Waiting, Capturing, TimedOut, Review }

    private readonly CalibrationHost _host;
    private readonly int _thresholdAtStart;
    private readonly int _offsetAtStart;
    private readonly string _folder;
    private readonly Recorder _recorder = new();
    private HighPassFilter? _liveFilter;
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly float[]?[] _swings = new float[SwingCount][];
    private readonly long[] _swingDetected = new long[SwingCount];
    private readonly TextBlock[] _swingStatus;
    private readonly Button[] _redo;
    private Step _step;
    private int _swingIndex;
    private long _roomStart;
    private long _roomEnd;
    private long _eventFrame;
    private long _captureEnd;
    private float[]? _room;
    private double _roomPeak;
    private double _detectLevel;
    private CalibrationReport? _report;
    private bool _dirty;
    private bool _closed;

    public CalibrationWindow(CalibrationHost host, AppSettings settings, string sessionFolder)
    {
        _host = host;
        _thresholdAtStart = settings.TriggerThreshold;
        _offsetAtStart = settings.ContactOffsetMs;
        var root = string.IsNullOrWhiteSpace(sessionFolder) ? AppPaths.SessionsRoot : sessionFolder;
        _folder = System.IO.Path.Combine(root, "Calibration", _started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        InitializeComponent();
        _swingStatus = [Swing1Status, Swing2Status, Swing3Status];
        _redo = [Redo1, Redo2, Redo3];
        _timeout.Tick += (_, _) => OnTimeout();
        Closing += (_, _) => OnClosing();
        ShowReady();
        Log.Info($"Calibration started with {host.MicrophoneName}; threshold {_thresholdAtStart}, offset {_offsetAtStart} ms, trigger above {HighPassFilter.CutoffHz:0} Hz.");
        _host.Begin(OnAudio);
    }

    public int? ProposedThreshold { get; private set; }

    private void OnAudio(byte[] data, WaveFormat format)
    {
        var samples = TakeWriter.ToFloat(data, format);
        var (start, end) = _recorder.Add(samples, format);
        if (_liveFilter is null || _liveFilter.SampleRate != format.SampleRate || _liveFilter.Channels != format.Channels)
        {
            _liveFilter = new HighPassFilter(format.SampleRate, format.Channels);
        }

        var peak = _liveFilter.Peak(samples, format.SampleRate);
        Dispatcher.BeginInvoke(() => OnPacket(peak, start, end));
    }

    private void OnPacket(double peak, long start, long end)
    {
        if (_closed)
        {
            return;
        }

        Level.Value = peak;
        LevelValue.Text = peak.ToString("0", CultureInfo.CurrentCulture);
        var rate = _recorder.SampleRate;
        switch (_step)
        {
            case Step.Room:
                var remaining = Math.Max(0, (_roomEnd - end) / (double)rate);
                Detail.Text = $"Stay quiet… {Math.Ceiling(remaining):0} s";
                if (end >= _roomEnd)
                {
                    FinishRoom();
                }

                break;
            case Step.Waiting:
                if (peak >= _detectLevel)
                {
                    _eventFrame = start;
                    _captureEnd = start + (long)(AfterSeconds * rate);
                    _timeout.Stop();
                    _step = Step.Capturing;
                    Prompt.Text = $"Heard swing {_swingIndex + 1}";
                    Detail.Text = "Recording the sounds that follow…";
                    UpdateRedo();
                }
                else
                {
                    _recorder.TrimBefore(end - (long)(KeepSeconds * rate));
                }

                break;
            case Step.Capturing:
                if (end >= _captureEnd)
                {
                    FinishSwing();
                }

                break;
            default:
                _recorder.TrimBefore(end - (long)(KeepSeconds * rate));
                break;
        }
    }

    private void ShowReady()
    {
        _step = Step.Ready;
        Prompt.Text = "Step 1: room noise";
        Detail.Text = "Stand at the ball as you would before a swing. Waggle, stay quiet, and click Start. The app records 5 seconds.";
        ActionButton.Content = "Start";
        ActionButton.Visibility = Visibility.Visible;
    }

    private void StartRoom()
    {
        if (_recorder.Format is null)
        {
            Detail.Text = "Waiting for the microphone… If the level bar stays still, check the mic.";
            return;
        }

        _roomStart = _recorder.Frames;
        _roomEnd = _roomStart + (long)(RoomSeconds * _recorder.SampleRate);
        _step = Step.Room;
        Prompt.Text = "Recording room noise";
        ActionButton.Visibility = Visibility.Collapsed;
        UpdateRedo();
    }

    private void FinishRoom()
    {
        var (samples, _) = _recorder.Take(_roomStart, _roomEnd);
        _room = samples;
        _roomPeak = SoundAnalysis.HighPassMeterPeak(samples, _recorder.Channels, _recorder.SampleRate);
        _detectLevel = Math.Clamp(Math.Max(_roomPeak * 4, 5), 5, 95);
        _dirty = true;
        RoomStatus.Text = $"Room noise above 2 kHz: loudest {_roomPeak:0.#}. A sound above {_detectLevel:0} counts as a swing.";
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"Calibration room noise recorded: loudest {_roomPeak:0.##} above 2 kHz, swing detect level {_detectLevel:0.##}."));
        StartSwing(NextMissing());
    }

    private void StartSwing(int index)
    {
        _swingIndex = index;
        _step = Step.Waiting;
        Prompt.Text = $"Hit ball {index + 1} of {SwingCount}";
        Detail.Text = _roomPeak * 4 > 95
            ? "The room is very loud for this mic; swings may not stand out. Swing normally. Waiting up to 30 seconds."
            : $"Swing normally. A sound above {_detectLevel:0} above 2 kHz counts as the strike. Waiting up to 30 seconds.";
        _swingStatus[index].Text = $"Swing {index + 1}: waiting…";
        ActionButton.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = false;
        _timeout.Stop();
        _timeout.Start();
        UpdateRedo();
    }

    private void OnTimeout()
    {
        _timeout.Stop();
        if (_step != Step.Waiting)
        {
            return;
        }

        _step = Step.TimedOut;
        Prompt.Text = "Nothing was heard";
        Detail.Text = $"No sound reached {_detectLevel:0} within 30 seconds. Check that the mic is plugged in and selected, then try again.";
        _swingStatus[_swingIndex].Text = $"Swing {_swingIndex + 1}: not heard";
        ActionButton.Content = "Try again";
        ActionButton.Visibility = Visibility.Visible;
        Log.Warn($"Calibration swing {_swingIndex + 1}: nothing reached {_detectLevel:0.#} within 30 s.");
        UpdateRedo();
    }

    private void FinishSwing()
    {
        var index = _swingIndex;
        var (samples, start) = _recorder.Take(_eventFrame - (long)(BeforeSeconds * _recorder.SampleRate), _captureEnd);
        _swings[index] = samples;
        _swingDetected[index] = _eventFrame - start;
        _dirty = true;
        var peak = SoundAnalysis.HighPassMeterPeak(samples, _recorder.Channels, _recorder.SampleRate);
        _swingStatus[index].Text = $"Swing {index + 1}: heard, peak {peak:0.#}";
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"Calibration swing {index + 1} captured: peak {peak:0.##}, {samples.Length / _recorder.Channels} frames."));
        var next = NextMissing();
        if (next >= 0)
        {
            StartSwing(next);
        }
        else
        {
            Finish();
        }
    }

    private void Finish()
    {
        _timeout.Stop();
        _step = Step.Review;
        Prompt.Text = "Calibration complete";
        Detail.Text = "Review the results. Apply sets the trigger threshold in Settings; it is saved when you click OK there.";
        ActionButton.Visibility = Visibility.Collapsed;
        _report = Analyze(complete: true);
        Save(_report);
        ShowReview(_report);
        UpdateRedo();
    }

    private CalibrationReport Analyze(bool complete)
    {
        var rate = _recorder.SampleRate;
        var channels = _recorder.Channels;
        var format = _recorder.Format;
        var report = new CalibrationReport
        {
            CreatedAt = _started,
            Complete = complete,
            Microphone = _host.MicrophoneName,
            SourceFormat = format is null ? null : $"{format.Encoding} {format.BitsPerSample}-bit",
            SampleRate = rate,
            Channels = channels,
            ThresholdAtStart = _thresholdAtStart,
            OffsetMsAtStart = _offsetAtStart,
            DetectLevel = Math.Round(_detectLevel, 3)
        };
        if (_room is not null)
        {
            report.Room = SoundAnalysis.Room(_room, channels, rate, "room.wav");
        }

        for (var i = 0; i < SwingCount; i++)
        {
            if (_swings[i] is float[] samples)
            {
                report.Swings.Add(SoundAnalysis.Swing(i + 1, samples, channels, rate, _swingDetected[i], report.Room, _detectLevel, $"swing{i + 1}.wav"));
            }
        }

        report.Advice = SoundAnalysis.Recommend(report.Room, report.Swings, channels, _host.WindowsCaptureLevel());
        return report;
    }

    private void Save(CalibrationReport report)
    {
        try
        {
            CalibrationStore.Save(_folder, report, _room, _swings, _recorder.SampleRate, _recorder.Channels);
            SavedTo.Text = "Saved to " + _folder;
        }
        catch (Exception ex)
        {
            Log.Error($"Saving calibration to {_folder} failed.", ex);
            SavedTo.Text = "Could not save: " + ex.Message;
        }

        CalibrationStore.LogReport(report, _folder);
        _dirty = false;
    }

    private void ShowReview(CalibrationReport report)
    {
        var advice = report.Advice!;
        ReviewPanel.Visibility = Visibility.Visible;
        if (advice.ProposedThreshold is int proposed)
        {
            var margin = advice.Margin is double value ? $"{value:0.0}x" : "no room noise measured";
            var gain = string.IsNullOrEmpty(advice.GainAdvice) ? "" : " " + advice.GainAdvice;
            Summary.Text = $"Proposed threshold: {proposed} (currently {_thresholdAtStart}). Loudest room sound {advice.RoomMeterPeak:0.#}; strikes {advice.QuietestStrike:0.#} to {advice.LoudestStrike:0.#}; margin {margin}.{gain}";
        }
        else
        {
            Summary.Text = "No threshold could be proposed.";
        }

        Warnings.Text = string.Join(Environment.NewLine, advice.Warnings.Select(text => "Warning: " + text));
        Warnings.Visibility = advice.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Notes.Text = string.Join(Environment.NewLine, advice.Notes);
        Notes.Visibility = advice.Notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = advice.ProposedThreshold is not null;
        Dispatcher.BeginInvoke(DrawChart, DispatcherPriority.Loaded);
    }

    private void DrawChart()
    {
        Chart.Children.Clear();
        var width = Chart.ActualWidth;
        var height = Chart.ActualHeight;
        if (_report is null || width <= 0 || height <= 0)
        {
            return;
        }

        const double top = 18;
        const double bottom = 4;
        var plotHeight = height - top - bottom;
        double Y(double level) => top + plotHeight * (1 - Math.Clamp(level, 0, 100) / 100);
        var panel = width / SwingCount;
        var proposed = _report.Advice?.ProposedThreshold;
        for (var i = 0; i < SwingCount; i++)
        {
            var left = panel * i + 6;
            var right = panel * (i + 1) - 6;
            if (i > 0)
            {
                Chart.Children.Add(new Line { X1 = panel * i, X2 = panel * i, Y1 = 0, Y2 = height, Stroke = DividerBrush, StrokeThickness = 1 });
            }

            var label = new TextBlock { Text = $"Swing {i + 1}", Foreground = Brushes.Gray, FontSize = 11 };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, 2);
            Chart.Children.Add(label);

            if (_swings[i] is not float[] samples)
            {
                continue;
            }

            var trace = SoundAnalysis.Trace(samples, _recorder.Channels, _recorder.SampleRate, 5);
            var durationMs = samples.Length / (double)_recorder.Channels / _recorder.SampleRate * 1000;
            double X(double ms) => left + (right - left) * Math.Clamp(ms / durationMs, 0, 1);
            var line = new Polyline { Stroke = TraceBrush, StrokeThickness = 1 };
            for (var k = 0; k < trace.Length; k++)
            {
                line.Points.Add(new Point(left + (right - left) * k / Math.Max(1, trace.Length - 1), Y(trace[k])));
            }

            Chart.Children.Add(line);
            Chart.Children.Add(new Line { X1 = left, X2 = right, Y1 = Y(_roomPeak), Y2 = Y(_roomPeak), Stroke = RoomBrush, StrokeThickness = 1, StrokeDashArray = [4, 3] });
            if (proposed is int threshold)
            {
                Chart.Children.Add(new Line { X1 = left, X2 = right, Y1 = Y(threshold), Y2 = Y(threshold), Stroke = ThresholdBrush, StrokeThickness = 1 });
            }

            var sound = _report.Swings.FirstOrDefault(swing => swing.Index == i + 1);
            if (sound is null)
            {
                continue;
            }

            foreach (var item in sound.Events)
            {
                var x = X(item.StartMs);
                var isStrike = item.Role == SoundAnalysis.StrikeRole;
                Chart.Children.Add(new Line
                {
                    X1 = x,
                    X2 = x,
                    Y1 = top,
                    Y2 = top + 8,
                    Stroke = isStrike ? ThresholdBrush : TraceBrush,
                    StrokeThickness = isStrike ? 2 : 1
                });
            }
        }
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_step == Step.Review)
        {
            DrawChart();
        }
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Ready)
        {
            StartRoom();
        }
        else if (_step == Step.TimedOut)
        {
            StartSwing(_swingIndex);
        }
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_step is Step.Room or Step.Capturing || sender is not Button { Tag: string tag })
        {
            return;
        }

        var index = int.Parse(tag, CultureInfo.InvariantCulture);
        _swings[index] = null;
        Log.Info($"Calibration swing {index + 1} redo.");
        StartSwing(index);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_report?.Advice?.ProposedThreshold is not int proposed)
        {
            return;
        }

        ProposedThreshold = proposed;
        DialogResult = true;
    }

    private void UpdateRedo()
    {
        var allowed = _step is not (Step.Room or Step.Capturing) && _room is not null;
        for (var i = 0; i < SwingCount; i++)
        {
            _redo[i].IsEnabled = allowed && _swings[i] is not null;
        }
    }

    private int NextMissing() => Array.FindIndex(_swings, samples => samples is null);

    private void OnClosing()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _timeout.Stop();
        _host.End();
        if (_dirty)
        {
            Save(Analyze(complete: false));
        }

        Log.Info(ProposedThreshold is int applied
            ? $"Calibration applied threshold {applied} (was {_thresholdAtStart})."
            : "Calibration closed without applying a threshold.");
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Recorder
    {
        private readonly object _gate = new();
        private readonly List<(long Start, float[] Samples)> _chunks = [];
        private long _frames;
        private WaveFormat? _format;

        public WaveFormat? Format
        {
            get
            {
                lock (_gate)
                {
                    return _format;
                }
            }
        }

        public int Channels => Math.Max(1, Format?.Channels ?? 1);

        public int SampleRate => Math.Max(1, Format?.SampleRate ?? 48000);

        public long Frames
        {
            get
            {
                lock (_gate)
                {
                    return _frames;
                }
            }
        }

        public (long Start, long End) Add(float[] samples, WaveFormat format)
        {
            lock (_gate)
            {
                _format ??= format;
                var start = _frames;
                _chunks.Add((start, samples));
                _frames += samples.Length / Math.Max(1, _format.Channels);
                return (start, _frames);
            }
        }

        public (float[] Samples, long Start) Take(long from, long to)
        {
            lock (_gate)
            {
                var channels = Math.Max(1, _format?.Channels ?? 1);
                var earliest = _chunks.Count > 0 ? _chunks[0].Start : _frames;
                from = Math.Max(from, earliest);
                to = Math.Min(to, _frames);
                if (to <= from)
                {
                    return ([], from);
                }

                var result = new float[(to - from) * channels];
                foreach (var (start, samples) in _chunks)
                {
                    var end = start + samples.Length / channels;
                    var overlapStart = Math.Max(from, start);
                    var overlapEnd = Math.Min(to, end);
                    if (overlapEnd <= overlapStart)
                    {
                        continue;
                    }

                    Array.Copy(samples, (overlapStart - start) * channels, result, (overlapStart - from) * channels, (overlapEnd - overlapStart) * channels);
                }

                return (result, from);
            }
        }

        public void TrimBefore(long frame)
        {
            lock (_gate)
            {
                var channels = Math.Max(1, _format?.Channels ?? 1);
                var remove = 0;
                while (remove < _chunks.Count && _chunks[remove].Start + _chunks[remove].Samples.Length / channels <= frame)
                {
                    remove++;
                }

                if (remove > 0)
                {
                    _chunks.RemoveRange(0, remove);
                }
            }
        }
    }
}
