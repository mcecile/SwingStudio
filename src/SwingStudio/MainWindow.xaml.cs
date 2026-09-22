using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwingStudio.Capture;
using SwingStudio.Session;
using SwingStudio.Settings;

namespace SwingStudio;

public partial class MainWindow : Window
{
    private const double MinPaneShare = 0.15;
    private const double MaxPaneShare = 0.85;
    private static readonly string[] PlaybackSpeeds = ["1/4", "1/3", "1/2", "1/1"];

    private readonly AppSettings _settings;
    private const string ChooseMicrophoneStatus = "Choose a microphone.";
    private const string MicrophoneUnavailableStatus = "The microphone is not available.";
    private const string StrikeStatus = "Strike";
    private const string PausedStatus = "Paused";

    private readonly CameraPreview _previewA;
    private readonly CameraPreview _previewB;
    private readonly MicrophoneLevelMeter _levelMeter = new();
    private readonly MonitorClock _clock = new();
    private readonly FrameRing _framesA = new();
    private readonly FrameRing _framesB = new();
    private readonly AudioRing _audio = new();
    private readonly DispatcherTimer _bufferTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _playbackClock = new();
    private static readonly SolidColorBrush ContactBrush = CreateContactBrush();
    private IReadOnlyList<CameraDevice> _cameras = [];
    private bool _suppressSelection;
    private bool _suppressMicrophone;
    private bool _closing;
    private volatile bool _strikeActive;
    private volatile bool _saving;
    private bool _playing;
    private bool _showingTake;
    private bool _sliderInternal;
    private bool _scrubbing;
    private string? _lastTakeFolder;
    private string? _loadedFolder;
    private string? _namingFolder;
    private bool _suppressSwingList;
    private int _replayIndex;
    private int _replayCount;
    private double _playheadMs;
    private double _previousPlayheadMs;
    private double _playbackStamp;
    private double _takeLengthMs;
    private double _playbackWindowStart;
    private double _playbackContactMs;
    private FrameSlice? _playbackA;
    private FrameSlice? _playbackB;
    private int _contactA = -1;
    private int _contactB = -1;
    private int _shownA = -1;
    private int _shownB = -1;
    private double _contactHoldA;
    private double _contactHoldB;
    private double _strikeStartMs;
    private double _strikeEndMs;
    private bool _levelWasBelow = true;

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        InitializeComponent();
        _previewA = new CameraPreview(Dispatcher, ShowFrameA, message => ShowPreviewError(true, message));
        _previewB = new CameraPreview(Dispatcher, ShowFrameB, message => ShowPreviewError(false, message));
        _bufferTimer.Tick += (_, _) => ShowBufferStatus();
        _bufferTimer.Start();
        _playbackTimer.Tick += (_, _) => AdvancePlayback();
        CameraADraw.SizeChanged += (_, _) => RedrawDrawings();
        CameraBDraw.SizeChanged += (_, _) => RedrawDrawings();
        UpdateDrawButtons();
        PlaybackSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => _scrubbing = true),
            true);
        PlaybackSlider.AddHandler(
            UIElement.PreviewMouseLeftButtonUpEvent,
            new System.Windows.Input.MouseButtonEventHandler((_, _) => _scrubbing = false),
            true);
        PlaybackSlider.LostMouseCapture += (_, _) => _scrubbing = false;
        ShowTransport();
        ShowMicrophone();
        RestoreLayout();
        RefreshCameras();
        ApplyPane(true);
        ApplyPane(false);
        SwingCatalog.Trim(_settings.SessionFolder, NormalizedSwingsToKeep());
        RefreshSwingList();
        Closing += (_, _) =>
        {
            _bufferTimer.Stop();
            _playbackTimer.Stop();
            _closing = true;
            EndPlayback();
            _playbackA?.Dispose();
            _playbackB?.Dispose();
            _playbackA = null;
            _playbackB = null;
            _levelMeter.Dispose();
            _previewA.Dispose();
            _previewB.Dispose();
            _framesA.Dispose();
            _framesB.Dispose();
            _audio.Dispose();
            SaveLayout();
        };
    }

    private void RestoreLayout()
    {
        var share = Math.Clamp(_settings.LeftPaneShare, MinPaneShare, MaxPaneShare);
        LeftPaneColumn.Width = new GridLength(share, GridUnitType.Star);
        RightPaneColumn.Width = new GridLength(1 - share, GridUnitType.Star);

        var cameraRowShare = Math.Clamp(_settings.CameraRowShare, MinPaneShare, MaxPaneShare);
        CameraRow.Height = new GridLength(cameraRowShare, GridUnitType.Star);
        PressureRow.Height = new GridLength(1 - cameraRowShare, GridUnitType.Star);

        var pressureShare = Math.Clamp(_settings.PressurePaneShare, MinPaneShare, MaxPaneShare);
        FootMapColumn.Width = new GridLength(pressureShare, GridUnitType.Star);
        WeightColumn.Width = new GridLength(1 - pressureShare, GridUnitType.Star);

        if (_settings.WindowWidth >= MinWidth)
        {
            Width = _settings.WindowWidth;
        }

        if (_settings.WindowHeight >= MinHeight)
        {
            Height = _settings.WindowHeight;
        }

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top && IsOnScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void PaneSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SaveLayout();
    }

    private void SaveLayout()
    {
        var left = LeftPaneColumn.ActualWidth;
        var right = RightPaneColumn.ActualWidth;
        if (left > 0 && right > 0)
        {
            _settings.LeftPaneShare = Math.Clamp(left / (left + right), MinPaneShare, MaxPaneShare);
        }

        var cameras = CameraRow.ActualHeight;
        var pressure = PressureRow.ActualHeight;
        if (cameras > 0 && pressure > 0)
        {
            _settings.CameraRowShare = Math.Clamp(cameras / (cameras + pressure), MinPaneShare, MaxPaneShare);
        }

        var footMap = FootMapColumn.ActualWidth;
        var weight = WeightColumn.ActualWidth;
        if (footMap > 0 && weight > 0)
        {
            _settings.PressurePaneShare = Math.Clamp(footMap / (footMap + weight), MinPaneShare, MaxPaneShare);
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        SettingsStore.Save(_settings);
    }

    private void SaveSwing_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_loadedFolder) || SwingCatalog.ReadSession(_loadedFolder) is not SwingSession session)
        {
            StatusDetail.Text = "Nothing to save yet.";
            return;
        }

        var folder = _loadedFolder;
        var suggested = session.Saved && !string.IsNullOrWhiteSpace(session.Name)
            ? session.Name.Trim()
            : session.StartedAt.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
        var dialog = new NameSwingWindow(suggested) { Owner = this };
        _namingFolder = folder;
        try
        {
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SwingCatalog.SaveName(folder, dialog.SwingName);
        }
        catch (Exception ex)
        {
            StatusDetail.Text = ex.Message;
            return;
        }
        finally
        {
            _namingFolder = null;
        }

        RefreshSwingList();
        if (SwingCatalog.SameFolder(folder, _loadedFolder))
        {
            StatusDetail.Text = dialog.SwingName;
        }
    }

    private void PlaybackSpeed_Click(object sender, RoutedEventArgs e)
    {
        var index = Array.IndexOf(PlaybackSpeeds, NormalizedPlaybackSpeed());
        _settings.PlaybackSpeed = PlaybackSpeeds[(index + 1) % PlaybackSpeeds.Length];
        SettingsStore.Save(_settings);
        ShowTransport();
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        var replays = NormalizedReplayCount();
        _settings.ReplaysBeforeLive = replays >= 3 ? 1 : replays + 1;
        SettingsStore.Save(_settings);
        ShowTransport();
    }

    private void ShowMicrophone()
    {
        var microphones = MicrophoneEnumerator.List();
        _suppressMicrophone = true;
        MicrophoneList.Items.Clear();
        MicrophoneList.Items.Add(new MicrophoneChoice(null, "(None)", null));
        foreach (var microphone in microphones)
        {
            MicrophoneList.Items.Add(new MicrophoneChoice(microphone.Id, microphone.Name, microphone.Name));
        }

        var selected = MicrophoneList.Items.Cast<MicrophoneChoice>().FirstOrDefault(choice => choice.Id == _settings.MicrophoneDeviceId);
        if (selected is null && !string.IsNullOrEmpty(_settings.MicrophoneDeviceId))
        {
            var storedName = _settings.MicrophoneFriendlyName;
            var label = string.IsNullOrWhiteSpace(storedName) ? "Saved microphone (not connected)" : $"{storedName} (not connected)";
            selected = new MicrophoneChoice(_settings.MicrophoneDeviceId, label, storedName);
            MicrophoneList.Items.Add(selected);
        }

        MicrophoneList.SelectedItem = selected ?? MicrophoneList.Items[0];
        _suppressMicrophone = false;
        ApplyMicrophoneLevel();
    }

    private void MicrophoneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMicrophone || MicrophoneList.SelectedItem is not MicrophoneChoice choice)
        {
            return;
        }

        _settings.MicrophoneDeviceId = choice.Id;
        _settings.MicrophoneFriendlyName = choice.StoredName;
        SettingsStore.Save(_settings);
        ApplyMicrophoneLevel();
    }

    private void ApplyMicrophoneLevel()
    {
        var choice = MicrophoneList.SelectedItem as MicrophoneChoice;
        var deviceId = choice?.Id;
        if (string.IsNullOrEmpty(deviceId))
        {
            _levelMeter.Stop();
            _audio.Clear();
            MicrophoneLevel.Value = 0;
            SetMicrophoneStatus(ChooseMicrophoneStatus);
            return;
        }

        if (_levelMeter.DeviceId == deviceId)
        {
            return;
        }

        try
        {
            _audio.Clear();
            _levelMeter.Start(deviceId, level =>
            {
                if (_closing)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => OnMicrophoneLevel(level));
            }, (data, format) =>
            {
                var durationMs = format.SampleRate <= 0 || format.BlockAlign <= 0
                    ? 0
                    : data.Length / (double)format.BlockAlign / format.SampleRate * 1000;
                _audio.Add(_clock.ElapsedMilliseconds - durationMs, data, format, RetentionSeconds());
            });
            SetMicrophoneStatus(null);
        }
        catch (Exception)
        {
            _levelMeter.Stop();
            _audio.Clear();
            MicrophoneLevel.Value = 0;
            SetMicrophoneStatus(MicrophoneUnavailableStatus);
        }
    }

    private void OnMicrophoneLevel(double level)
    {
        if (_closing)
        {
            return;
        }

        MicrophoneLevel.Value = level;
        var below = level < _settings.TriggerThreshold;
        if (_strikeActive || _saving || _playing || _drawTool is not null)
        {
            _levelWasBelow = below;
            return;
        }

        if (_levelWasBelow && !below)
        {
            if (_showingTake)
            {
                EndPlayback();
            }

            var now = _clock.ElapsedMilliseconds;
            _strikeStartMs = now;
            _strikeEndMs = now + Math.Max(0.1, _settings.SecondsAfterImpact) * 1000;
            _strikeActive = true;
            StatusDetail.Text = StrikeStatus;
        }

        _levelWasBelow = below;
    }

    private double RetentionSeconds()
    {
        if (!_strikeActive)
        {
            return _settings.SecondsBeforeImpact;
        }

        var elapsed = (_clock.ElapsedMilliseconds - _strikeStartMs) / 1000;
        return _settings.SecondsBeforeImpact + Math.Max(0, elapsed);
    }

    private void ShowBufferStatus()
    {
        if (_closing || StatusDetail.Text is ChooseMicrophoneStatus or MicrophoneUnavailableStatus)
        {
            return;
        }

        if (_strikeActive)
        {
            if (_clock.ElapsedMilliseconds < _strikeEndMs)
            {
                return;
            }

            BeginSave();
            return;
        }

        var windowMs = Math.Max(0.1, _settings.SecondsBeforeImpact) * 1000;
        var now = _clock.ElapsedMilliseconds;
        double? span = null;
        if (!string.IsNullOrEmpty(_settings.CameraADevicePath))
        {
            span = _framesA.SpanMilliseconds(now, windowMs);
        }

        if (!string.IsNullOrEmpty(_settings.CameraBDevicePath))
        {
            var other = _framesB.SpanMilliseconds(now, windowMs);
            span = span is null ? other : Math.Min(span.Value, other);
        }

        if (span is null)
        {
            if (IsBufferStatus(StatusDetail.Text))
            {
                StatusDetail.Text = "";
            }

            return;
        }

        if (StatusDetail.Text.Length > 0 && !IsBufferStatus(StatusDetail.Text))
        {
            return;
        }

        var seconds = span.Value / 1000;
        StatusDetail.Text = "Buffer " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    private void BeginSave()
    {
        var windowStart = _strikeStartMs - Math.Max(0.1, _settings.SecondsBeforeImpact) * 1000;
        var windowEnd = _strikeEndMs;
        var triggerMs = _strikeStartMs - windowStart;
        var offsetMs = _settings.ContactOffsetMs;
        var keep = NormalizedSwingsToKeep();
        var sessionRoot = _settings.SessionFolder;
        var framesA = _framesA.Slice(windowStart, windowEnd);
        var framesB = string.IsNullOrEmpty(_settings.CameraBDevicePath) ? new FrameSlice() : _framesB.Slice(windowStart, windowEnd);
        var audio = _audio.Slice(windowStart, windowEnd);
        var session = new SwingSession
        {
            StartedAt = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Math.Max(0, windowEnd - windowStart)),
            CameraADevicePath = _settings.CameraADevicePath,
            CameraBDevicePath = _settings.CameraBDevicePath,
            RequestedMode = new CaptureMode
            {
                Width = _settings.CaptureWidth,
                Height = _settings.CaptureHeight,
                FramesPerSecond = _settings.CaptureFramesPerSecond,
                FourCc = _settings.CaptureFourCc
            },
            MicrophoneDeviceId = _settings.MicrophoneDeviceId,
            SessionRoot = _settings.SessionFolder
        };

        _strikeActive = false;
        _saving = true;
        StatusDetail.Text = "Saving swing…";
        Task.Run(() =>
        {
            try
            {
                TakeWriter.Write(session, framesA, framesB, audio, windowStart, triggerMs, offsetMs);
                SwingCatalog.Trim(sessionRoot, keep, session.FolderPath, _namingFolder);
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing)
                    {
                        framesA.Dispose();
                        framesB.Dispose();
                        _saving = false;
                        return;
                    }

                    _lastTakeFolder = session.FolderPath;
                    _loadedFolder = session.FolderPath;
                    LoadDrawings();
                    RefreshSwingList();
                    StartPlayback(framesA, framesB, windowStart, session.ContactMs ?? triggerMs);
                });
            }
            catch (Exception ex)
            {
                framesA.Dispose();
                framesB.Dispose();
                Dispatcher.BeginInvoke(() =>
                {
                    _saving = false;
                    if (!_closing)
                    {
                        StatusDetail.Text = ex.Message;
                    }
                });
            }
        });
    }

    private void StartPlayback(FrameSlice framesA, FrameSlice framesB, double windowStart, double contactMs)
    {
        if (!HoldFrames(framesA, framesB, windowStart, contactMs))
        {
            _saving = false;
            StatusDetail.Text = "";
            return;
        }

        _saving = false;
        ResumePlayback(fromStart: true);
    }

    private bool HoldFrames(FrameSlice framesA, FrameSlice framesB, double windowStart, double contactMs)
    {
        var length = TakeLength(framesA, framesB, windowStart);
        if (length <= 0)
        {
            framesA.Dispose();
            framesB.Dispose();
            return false;
        }

        if (!ReferenceEquals(_playbackA, framesA))
        {
            _playbackA?.Dispose();
            _playbackA = framesA;
        }

        if (!ReferenceEquals(_playbackB, framesB))
        {
            _playbackB?.Dispose();
            _playbackB = framesB;
        }

        _playbackWindowStart = windowStart;
        _playbackContactMs = contactMs;
        _contactA = ContactIndex(framesA, contactMs, windowStart);
        _contactB = ContactIndex(framesB, contactMs, windowStart);
        _takeLengthMs = length;
        return true;
    }

    private void SwingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSwingList || SwingList.SelectedItem is not SwingEntry entry)
        {
            return;
        }

        if (SwingCatalog.SameFolder(entry.FolderPath, _loadedFolder))
        {
            return;
        }

        if (_saving || _strikeActive)
        {
            ReselectLoadedSwing();
            return;
        }

        LoadSwing(entry.FolderPath);
    }

    private void LoadSwing(string folder)
    {
        _playing = false;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        _saving = true;
        StatusDetail.Text = "Opening swing…";
        Task.Run(() =>
        {
            try
            {
                var loaded = TakeReader.Read(folder);
                Dispatcher.BeginInvoke(() =>
                {
                    if (_closing)
                    {
                        loaded.Dispose();
                        _saving = false;
                        return;
                    }

                    if (!HoldFrames(loaded.CameraA, loaded.CameraB, 0, loaded.ContactMs))
                    {
                        _saving = false;
                        StatusDetail.Text = "This swing has no video.";
                        RefreshSwingList();
                        return;
                    }

                    _loadedFolder = folder;
                    _lastTakeFolder = folder;
                    LoadDrawings();
                    _saving = false;
                    PauseAtStart();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _saving = false;
                    if (!_closing)
                    {
                        StatusDetail.Text = ex.Message;
                        RefreshSwingList();
                    }
                });
            }
        });
    }

    private void PauseAtStart()
    {
        _playing = false;
        _showingTake = true;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        _replayCount = NormalizedReplayCount();
        _replayIndex = 0;
        _playheadMs = 0;
        _previousPlayheadMs = 0;
        _shownA = -1;
        _shownB = -1;
        _contactHoldA = 0;
        _contactHoldB = 0;
        StatusDetail.Text = PausedStatus;
        ShowCurrentFrame();
        ShowPlaybackControls();
    }

    private void RefreshSwingList()
    {
        var entries = SwingCatalog.List(_settings.SessionFolder);
        _suppressSwingList = true;
        SwingList.ItemsSource = entries;
        SwingList.SelectedItem = entries.FirstOrDefault(entry => SwingCatalog.SameFolder(entry.FolderPath, _loadedFolder));
        _suppressSwingList = false;
    }

    private void ReselectLoadedSwing()
    {
        _suppressSwingList = true;
        if (SwingList.ItemsSource is IEnumerable<SwingEntry> entries)
        {
            SwingList.SelectedItem = entries.FirstOrDefault(entry => SwingCatalog.SameFolder(entry.FolderPath, _loadedFolder));
        }

        _suppressSwingList = false;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!HasTake())
        {
            return;
        }

        if (_playing)
        {
            PausePlayback();
            return;
        }

        ExitDrawMode();
        ResumePlayback(fromStart: !_showingTake);
    }

    private void BackFrame_Click(object sender, RoutedEventArgs e) => StepFrame(-1);

    private void ForwardFrame_Click(object sender, RoutedEventArgs e) => StepFrame(1);

    private void PlaybackSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderInternal || _closing || !HasTake())
        {
            return;
        }

        _previousPlayheadMs = PlaybackSlider.Value;
        _playheadMs = PlaybackSlider.Value;
        _contactHoldA = 0;
        _contactHoldB = 0;
        if (!_playing)
        {
            if (!_showingTake)
            {
                _replayIndex = 0;
                _replayCount = NormalizedReplayCount();
            }

            _showingTake = true;
            _playbackTimer.Stop();
            _playbackClock.Stop();
            StatusDetail.Text = PausedStatus;
        }

        ShowCurrentFrame();
        ShowPlaybackControls();
    }

    private void StepFrame(int direction)
    {
        if (!HasTake())
        {
            return;
        }

        var times = FrameTimes();
        if (times.Count == 0)
        {
            return;
        }

        var wasShowing = _showingTake;
        var current = DisplayedSessionTime();
        double target;
        if (!wasShowing)
        {
            target = direction < 0 ? times[^1] : times[0];
            _replayIndex = 0;
            _replayCount = NormalizedReplayCount();
        }
        else if (direction < 0)
        {
            target = times[0];
            for (var i = times.Count - 1; i >= 0; i--)
            {
                if (times[i] < current - 0.05)
                {
                    target = times[i];
                    break;
                }
            }
        }
        else
        {
            target = times[^1];
            foreach (var time in times)
            {
                if (time > current + 0.05)
                {
                    target = time;
                    break;
                }
            }
        }

        _playing = false;
        _showingTake = true;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        StatusDetail.Text = PausedStatus;
        _previousPlayheadMs = target;
        _playheadMs = target;
        _contactHoldA = 0;
        _contactHoldB = 0;
        ShowCurrentFrame();
        ShowPlaybackControls();
    }

    private void PausePlayback()
    {
        _playing = false;
        _showingTake = true;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        StatusDetail.Text = PausedStatus;
        ShowPlaybackControls();
    }

    private void ResumePlayback(bool fromStart)
    {
        if (fromStart)
        {
            _replayCount = NormalizedReplayCount();
            _replayIndex = 0;
            _playheadMs = 0;
            _previousPlayheadMs = 0;
            _shownA = -1;
            _shownB = -1;
            _contactHoldA = 0;
            _contactHoldB = 0;
            _playbackClock.Restart();
        }
        else if (!_playbackClock.IsRunning)
        {
            _playbackClock.Start();
        }

        _playing = true;
        _showingTake = true;
        _saving = false;
        StatusDetail.Text = ReplayStatus();
        _playbackStamp = _playbackClock.Elapsed.TotalMilliseconds;
        _playbackTimer.Start();
        ShowCurrentFrame();
        ShowPlaybackControls();
    }

    private void AdvancePlayback()
    {
        if (!_playing || _closing || _scrubbing)
        {
            if (_scrubbing)
            {
                _playbackStamp = _playbackClock.Elapsed.TotalMilliseconds;
            }

            return;
        }

        var elapsed = _playbackClock.Elapsed.TotalMilliseconds;
        var delta = Math.Max(0, elapsed - _playbackStamp);
        _playbackStamp = elapsed;
        _previousPlayheadMs = _playheadMs;
        _playheadMs += delta * PlaybackRate();
        if (_playheadMs > _takeLengthMs)
        {
            _replayIndex++;
            if (_replayIndex >= _replayCount)
            {
                EndPlayback();
                return;
            }

            _playheadMs = 0;
            _previousPlayheadMs = 0;
            _shownA = -1;
            _shownB = -1;
            _contactHoldA = 0;
            _contactHoldB = 0;
            StatusDetail.Text = ReplayStatus();
        }

        ShowCurrentFrame();
        ShowPlaybackControls();
    }

    private void ShowCurrentFrame()
    {
        ShowPlaybackPane(_playbackA, CameraAImage, CameraAOverlay, _contactA, ref _shownA, ref _contactHoldA);
        ShowPlaybackPane(_playbackB, CameraBImage, CameraBOverlay, _contactB, ref _shownB, ref _contactHoldB);
        RedrawDrawings();
    }

    private void ShowPlaybackPane(
        FrameSlice? slice,
        Image image,
        Canvas overlay,
        int contactIndex,
        ref int shownIndex,
        ref double contactHold)
    {
        if (slice is null || slice.Frames.Count == 0)
        {
            return;
        }

        var index = FrameAt(slice, _playheadMs, _playbackWindowStart);
        if (index != shownIndex)
        {
            shownIndex = index;
            image.Source = CameraPreview.CopyFrame(slice.Frames[index].Frame);
        }

        var now = _playbackClock.Elapsed.TotalMilliseconds;
        var crossed = _playing && _previousPlayheadMs < _playbackContactMs && _playheadMs >= _playbackContactMs;
        if (_playing && (index == contactIndex || crossed))
        {
            contactHold = Math.Max(contactHold, now + 160);
        }

        SetContactLine(overlay, _playing ? now < contactHold : index == contactIndex);
    }

    private void EndPlayback()
    {
        _playing = false;
        _showingTake = false;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        _playheadMs = 0;
        _previousPlayheadMs = 0;
        _shownA = -1;
        _shownB = -1;
        _contactHoldA = 0;
        _contactHoldB = 0;
        SetContactLine(CameraAOverlay, false);
        SetContactLine(CameraBOverlay, false);
        ExitDrawMode();
        RedrawDrawings();
        _saving = false;
        if (!_closing && (StatusDetail.Text.StartsWith("Replay ", StringComparison.Ordinal) || StatusDetail.Text == PausedStatus))
        {
            StatusDetail.Text = "";
        }

        ShowPlaybackControls();
    }

    private bool HasTake() => _takeLengthMs > 0 && (_playbackA is { Frames.Count: > 0 } || _playbackB is { Frames.Count: > 0 });

    private List<double> FrameTimes()
    {
        var times = new List<double>();
        AddFrameTimes(_playbackA, times);
        AddFrameTimes(_playbackB, times);
        times.Sort();
        var unique = new List<double>();
        foreach (var time in times)
        {
            if (unique.Count == 0 || time - unique[^1] > 0.05)
            {
                unique.Add(time);
            }
        }

        return unique;
    }

    private void AddFrameTimes(FrameSlice? slice, List<double> times)
    {
        if (slice is null)
        {
            return;
        }

        foreach (var frame in slice.Frames)
        {
            times.Add(frame.TimeMs - _playbackWindowStart);
        }
    }

    private double DisplayedSessionTime()
    {
        double? time = null;
        if (_playbackA is { Frames.Count: > 0 })
        {
            time = _playbackA.Frames[FrameAt(_playbackA, _playheadMs, _playbackWindowStart)].TimeMs - _playbackWindowStart;
        }

        if (_playbackB is { Frames.Count: > 0 })
        {
            var other = _playbackB.Frames[FrameAt(_playbackB, _playheadMs, _playbackWindowStart)].TimeMs - _playbackWindowStart;
            time = time is null ? other : Math.Max(time.Value, other);
        }

        return time ?? _playheadMs;
    }

    private void ShowPlaybackControls()
    {
        if (_closing)
        {
            return;
        }

        var hasTake = HasTake();
        BackFrameButton.IsEnabled = hasTake;
        ForwardFrameButton.IsEnabled = hasTake;
        PlayPauseButton.IsEnabled = hasTake;
        PlaybackSlider.IsEnabled = hasTake;
        UpdateDrawButtons();
        PlayPauseIcon.Text = _playing ? "\uE103" : "\uE102";
        var playLabel = _playing ? "Pause" : "Play";
        PlayPauseButton.ToolTip = playLabel;
        System.Windows.Automation.AutomationProperties.SetName(PlayPauseButton, playLabel);
        if (!hasTake)
        {
            return;
        }

        _sliderInternal = true;
        PlaybackSlider.Maximum = Math.Max(_takeLengthMs, 1);
        if (!_scrubbing)
        {
            PlaybackSlider.Value = Math.Clamp(_playheadMs, 0, PlaybackSlider.Maximum);
        }

        _sliderInternal = false;
    }

    private string ReplayStatus() => $"Replay {_replayIndex + 1} of {_replayCount}";

    private double PlaybackRate()
    {
        return _settings.PlaybackSpeed switch
        {
            "1/4" => 0.25,
            "1/3" => 1d / 3d,
            "1/2" => 0.5,
            _ => 1
        };
    }

    private static int FrameAt(FrameSlice slice, double playheadMs, double windowStart)
    {
        var index = 0;
        for (var i = 0; i < slice.Frames.Count; i++)
        {
            if (slice.Frames[i].TimeMs - windowStart <= playheadMs)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    private static int ContactIndex(FrameSlice slice, double contactMs, double windowStart)
    {
        if (slice.Frames.Count == 0)
        {
            return -1;
        }

        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < slice.Frames.Count; i++)
        {
            var distance = Math.Abs(slice.Frames[i].TimeMs - windowStart - contactMs);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private static double TakeLength(FrameSlice framesA, FrameSlice framesB, double windowStart)
    {
        var length = 0d;
        if (framesA.Frames.Count > 0)
        {
            length = Math.Max(length, framesA.Frames[^1].TimeMs - windowStart);
        }

        if (framesB.Frames.Count > 0)
        {
            length = Math.Max(length, framesB.Frames[^1].TimeMs - windowStart);
        }

        return length;
    }

    private static void SetContactLine(Canvas overlay, bool show)
    {
        overlay.Children.Clear();
        if (!show || overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
        {
            return;
        }

        var y = overlay.ActualHeight / 2;
        overlay.Children.Add(new Line
        {
            X1 = 0,
            X2 = overlay.ActualWidth,
            Y1 = y,
            Y2 = y,
            Stroke = ContactBrush,
            StrokeThickness = 3
        });
        var label = new TextBlock
        {
            Text = "IMPACT",
            Foreground = ContactBrush,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(label, 8);
        Canvas.SetTop(label, y - 22);
        overlay.Children.Add(label);
    }

    private static SolidColorBrush CreateContactBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(255, 224, 138));
        brush.Freeze();
        return brush;
    }

    private static bool IsBufferStatus(string text) => text.StartsWith("Buffer ", StringComparison.Ordinal);

    private void SetMicrophoneStatus(string? message)
    {
        if (message is not null)
        {
            StatusDetail.Text = message;
            return;
        }

        if (StatusDetail.Text is ChooseMicrophoneStatus or MicrophoneUnavailableStatus)
        {
            StatusDetail.Text = "";
        }
    }

    private void MicrophoneList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MicrophoneList.IsDropDownOpen)
        {
            return;
        }

        ShowMicrophone();
    }

    private sealed record MicrophoneChoice(string? Id, string Name, string? StoredName)
    {
        public override string ToString() => Name;
    }

    private void ShowTransport()
    {
        _settings.PlaybackSpeed = NormalizedPlaybackSpeed();
        _settings.ReplaysBeforeLive = NormalizedReplayCount();
        PlaybackSpeedValue.Text = _settings.PlaybackSpeed;
        ReplayValue.Text = $"{_settings.ReplaysBeforeLive}x";
    }

    private string NormalizedPlaybackSpeed()
    {
        return Array.IndexOf(PlaybackSpeeds, _settings.PlaybackSpeed) >= 0 ? _settings.PlaybackSpeed : "1/1";
    }

    private int NormalizedReplayCount()
    {
        return Math.Clamp(_settings.ReplaysBeforeLive, 1, 3);
    }

    private int NormalizedSwingsToKeep()
    {
        return Math.Clamp(_settings.SwingsToKeep <= 0 ? 10 : _settings.SwingsToKeep, 1, 50);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var width = _settings.CaptureWidth;
        var height = _settings.CaptureHeight;
        var framesPerSecond = _settings.CaptureFramesPerSecond;
        var fourCc = _settings.CaptureFourCc;
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SettingsStore.Save(_settings);
        SwingCatalog.Trim(_settings.SessionFolder, NormalizedSwingsToKeep(), _loadedFolder);
        RefreshSwingList();
        if (width != _settings.CaptureWidth
            || height != _settings.CaptureHeight
            || Math.Abs(framesPerSecond - _settings.CaptureFramesPerSecond) > 0.1
            || !string.Equals(fourCc, _settings.CaptureFourCc, StringComparison.OrdinalIgnoreCase))
        {
            ApplyPane(true);
            ApplyPane(false);
        }
    }

    private void CameraAList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignFromList(true, CameraAList, CameraAMessage);
    }

    private void CameraBList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AssignFromList(false, CameraBList, CameraBMessage);
    }

    private void CameraList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false })
        {
            RefreshCameras();
        }
    }

    private void AssignFromList(bool isA, ComboBox list, TextBlock message)
    {
        if (_suppressSelection)
        {
            return;
        }

        var camera = list.SelectedItem as CameraDevice;
        if (camera is null)
        {
            return;
        }

        var otherPath = isA ? _settings.CameraBDevicePath : _settings.CameraADevicePath;
        var ownPath = isA ? _settings.CameraADevicePath : _settings.CameraBDevicePath;
        if (camera.DevicePath == otherPath)
        {
            message.Text = "That camera is already used in the other pane.";
            Select(list, _cameras.FirstOrDefault(item => item.DevicePath == ownPath));
            return;
        }

        if (isA)
        {
            _settings.CameraADevicePath = camera.DevicePath;
            _settings.CameraAFriendlyName = camera.Name;
        }
        else
        {
            _settings.CameraBDevicePath = camera.DevicePath;
            _settings.CameraBFriendlyName = camera.Name;
        }

        SettingsStore.Save(_settings);
        ApplyPane(isA);
    }

    private void RefreshCameras()
    {
        _cameras = CameraEnumerator.List();
        _suppressSelection = true;
        CameraAList.ItemsSource = _cameras;
        CameraBList.ItemsSource = _cameras;
        CameraAList.SelectedItem = _cameras.FirstOrDefault(camera => camera.DevicePath == _settings.CameraADevicePath);
        CameraBList.SelectedItem = _cameras.FirstOrDefault(camera => camera.DevicePath == _settings.CameraBDevicePath);
        _suppressSelection = false;
    }

    private void ApplyPane(bool isA)
    {
        var preview = isA ? _previewA : _previewB;
        var image = isA ? CameraAImage : CameraBImage;
        var picker = isA ? CameraAPicker : CameraBPicker;
        var message = isA ? CameraAMessage : CameraBMessage;
        var stats = isA ? CameraAStats : CameraBStats;
        var path = isA ? _settings.CameraADevicePath : _settings.CameraBDevicePath;

        var gear = isA ? CameraASettings : CameraBSettings;
        var ring = isA ? _framesA : _framesB;
        preview.Stop();
        ring.Clear();
        image.Source = null;
        stats.Text = "";
        gear.IsEnabled = false;

        if (string.IsNullOrEmpty(path))
        {
            picker.Visibility = Visibility.Visible;
            message.Text = _cameras.Count == 0 ? "No cameras found." : "";
            return;
        }

        var camera = _cameras.FirstOrDefault(item => item.DevicePath == path);
        if (camera is null)
        {
            picker.Visibility = Visibility.Visible;
            message.Text = "Saved camera was not found.";
            return;
        }

        picker.Visibility = Visibility.Collapsed;
        message.Text = "";
        stats.Text = "Opening…";
        gear.IsEnabled = true;
        if (isA)
        {
            try
            {
                CameraModeLister.Remember(path, CameraModeLister.List(path));
            }
            catch (Exception)
            {
                CameraModeLister.Remember(path, []);
            }
        }

        _settings.CameraControls.TryGetValue(path, out var controls);
        preview.Start(camera.Index, _settings.CaptureWidth, _settings.CaptureHeight, _settings.CaptureFramesPerSecond, _settings.CaptureFourCc, controls, frame =>
        {
            ring.Add(_clock.ElapsedMilliseconds, frame, RetentionSeconds());
        });
    }

    private void CameraASettings_Click(object sender, RoutedEventArgs e) => OpenCameraSettings(true);

    private void CameraBSettings_Click(object sender, RoutedEventArgs e) => OpenCameraSettings(false);

    private void OpenCameraSettings(bool isA)
    {
        var path = isA ? _settings.CameraADevicePath : _settings.CameraBDevicePath;
        var preview = isA ? _previewA : _previewB;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var opened = preview.RequestSettings(values =>
        {
            _settings.CameraControls[path] = new Dictionary<string, double>(values);
            SettingsStore.Save(_settings);
            StatusDetail.Text = "Camera settings saved.";
        });
        if (!opened)
        {
            StatusDetail.Text = "The camera is not open.";
        }
    }

    private void ShowFrameA(BitmapSource bitmap, int width, int height, double framesPerSecond) =>
        ShowFrame(CameraAImage, CameraAStats, CameraAPicker, bitmap, width, height, framesPerSecond);

    private void ShowFrameB(BitmapSource bitmap, int width, int height, double framesPerSecond) =>
        ShowFrame(CameraBImage, CameraBStats, CameraBPicker, bitmap, width, height, framesPerSecond);

    private void ShowFrame(Image image, TextBlock stats, StackPanel picker, BitmapSource bitmap, int width, int height, double framesPerSecond)
    {
        if (_closing || (_showingTake && IsPlaybackImage(image)))
        {
            return;
        }

        image.Source = bitmap;
        picker.Visibility = Visibility.Collapsed;
        stats.Text = framesPerSecond > 0
            ? $"{width}×{height}   {framesPerSecond:0} fps"
            : $"{width}×{height}";
    }

    private bool IsPlaybackImage(Image image)
    {
        if (image == CameraAImage)
        {
            return _playbackA is { Frames.Count: > 0 };
        }

        if (image == CameraBImage)
        {
            return _playbackB is { Frames.Count: > 0 };
        }

        return false;
    }

    private void ShowPreviewError(bool isA, string message)
    {
        if (_closing)
        {
            return;
        }

        var picker = isA ? CameraAPicker : CameraBPicker;
        var error = isA ? CameraAMessage : CameraBMessage;
        var stats = isA ? CameraAStats : CameraBStats;
        picker.Visibility = Visibility.Visible;
        error.Text = message;
        stats.Text = "";
    }

    private void Select(ComboBox list, CameraDevice? camera)
    {
        _suppressSelection = true;
        list.SelectedItem = camera;
        _suppressSelection = false;
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        return left + width > screenLeft + 80
            && top + height > screenTop + 80
            && left < screenRight - 80
            && top < screenBottom - 80;
    }
}
