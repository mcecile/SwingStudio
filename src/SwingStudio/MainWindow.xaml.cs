using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using SwingStudio.Capture;
using SwingStudio.Settings;

namespace SwingStudio;

public partial class MainWindow : Window
{
    private const double MinPaneShare = 0.15;
    private const double MaxPaneShare = 0.85;
    private static readonly string[] PlaybackSpeeds = ["1/4", "1/3", "1/2", "1/1"];

    private readonly AppSettings _settings;
    private readonly CameraPreview _previewA;
    private readonly CameraPreview _previewB;
    private IReadOnlyList<CameraDevice> _cameras = [];
    private bool _suppressSelection;
    private bool _suppressMicrophone;
    private bool _closing;

    public MainWindow()
    {
        _settings = SettingsStore.Load();
        InitializeComponent();
        _previewA = new CameraPreview(Dispatcher, ShowFrameA, message => ShowPreviewError(true, message));
        _previewB = new CameraPreview(Dispatcher, ShowFrameB, message => ShowPreviewError(false, message));
        ShowTransport();
        ShowMicrophone();
        RestoreLayout();
        RefreshCameras();
        ApplyPane(true);
        ApplyPane(false);
        Closing += (_, _) =>
        {
            _closing = true;
            _previewA.Dispose();
            _previewB.Dispose();
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
        StatusDetail.Text = "Nothing to save yet.";
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
        preview.Stop();
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
        preview.Start(camera.Index, _settings.CaptureWidth, _settings.CaptureHeight, _settings.CaptureFramesPerSecond, _settings.CaptureFourCc, controls);
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
        if (_closing)
        {
            return;
        }

        image.Source = bitmap;
        picker.Visibility = Visibility.Collapsed;
        stats.Text = framesPerSecond > 0
            ? $"{width}×{height}   {framesPerSecond:0} fps"
            : $"{width}×{height}";
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
