using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SwingStudio.Capture;
using SwingStudio.Session;
using SwingStudio.Settings;

namespace SwingStudio;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CalibrationHost? _calibration;

    public SettingsWindow(AppSettings settings, CalibrationHost? calibration = null)
    {
        _settings = settings;
        _calibration = calibration;
        InitializeComponent();
        ThresholdSlider.Value = Math.Clamp(settings.TriggerThreshold, 0, 100);
        SelectTrigger(TriggerSources.Normalize(settings.TriggerSource));
        SecondsBefore.Text = settings.SecondsBeforeImpact.ToString("0.0", CultureInfo.InvariantCulture);
        SecondsAfter.Text = settings.SecondsAfterImpact.ToString("0.0", CultureInfo.InvariantCulture);
        SessionFolder.Text = settings.SessionFolder;
        SwingsToKeep.Text = Math.Clamp(settings.SwingsToKeep <= 0 ? 10 : settings.SwingsToKeep, 1, 50).ToString(CultureInfo.InvariantCulture);

        var offered = CameraModeLister.Recall(settings.CameraADevicePath).ToList();
        var cameraReportedModes = offered.Count > 0;
        var selected = offered.FirstOrDefault(mode => mode.Matches(
            settings.CaptureWidth,
            settings.CaptureHeight,
            settings.CaptureFramesPerSecond,
            settings.CaptureFourCc));
        if (selected is null)
        {
            selected = new CaptureMode
            {
                Width = settings.CaptureWidth,
                Height = settings.CaptureHeight,
                FramesPerSecond = settings.CaptureFramesPerSecond,
                FourCc = settings.CaptureFourCc,
                IsSavedRequest = cameraReportedModes
            };
            offered.Insert(0, selected);
        }

        CaptureModeList.ItemsSource = offered;
        CaptureModeList.SelectedItem = selected;
        CaptureModeHint.Text = CaptureModeNote(settings, cameraReportedModes);
    }

    private static string CaptureModeNote(AppSettings settings, bool cameraReportedModes)
    {
        if (string.IsNullOrEmpty(settings.CameraADevicePath))
        {
            return "Assign Camera A to list its modes. Both cameras use the mode you pick.";
        }

        var name = string.IsNullOrEmpty(settings.CameraAFriendlyName) ? "Camera A" : settings.CameraAFriendlyName;
        return cameraReportedModes
            ? $"Modes from {name}. Both cameras use the mode you pick."
            : $"{name} has not reported its modes yet. Both cameras use the mode you pick.";
    }

    private void TriggerSourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThresholdSlider is null || CalibrateButton is null || CalibrateHint is null)
        {
            return;
        }

        ApplyTriggerControls();
    }

    private void ApplyTriggerControls()
    {
        var launchMonitor = SelectedTrigger() == TriggerSources.LaunchMonitor;
        ThresholdSlider.IsEnabled = !launchMonitor;
        if (launchMonitor)
        {
            CalibrateButton.IsEnabled = false;
            CalibrateHint.Text = "ProTee VX Labs arms the take when the ball moves. Npcap must be installed.";
            return;
        }

        CalibrateButton.IsEnabled = _calibration?.HasMicrophone == true;
        CalibrateHint.Text = CalibrateButton.IsEnabled ? "3 swings; listens above 2 kHz" : "Choose a working microphone first.";
    }

    private void SelectTrigger(string source)
    {
        foreach (ComboBoxItem item in TriggerSourceList.Items)
        {
            if (item.Tag as string == source)
            {
                TriggerSourceList.SelectedItem = item;
                return;
            }
        }

        TriggerSourceList.SelectedIndex = 0;
    }

    private string SelectedTrigger() =>
        (TriggerSourceList.SelectedItem as ComboBoxItem)?.Tag as string ?? TriggerSources.Microphone;

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdValue is null)
        {
            return;
        }

        ThresholdValue.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = SessionFolder.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "Session folder",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() == true)
        {
            SessionFolder.Text = dialog.FolderName;
        }
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        if (_calibration is not { HasMicrophone: true })
        {
            return;
        }

        var folder = SessionFolder.Text.Trim();
        var window = new CalibrationWindow(_calibration, _settings, string.IsNullOrEmpty(folder) ? _settings.SessionFolder : folder) { Owner = this };
        if (window.ShowDialog() == true && window.ProposedThreshold is int threshold)
        {
            ThresholdSlider.Value = threshold;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("The log folder could not be opened.", ex);
            ShowInvalid(ex.Message);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSeconds(SecondsBefore.Text, out var before))
        {
            ShowInvalid("Seconds before impact must be between 0.1 and 10.");
            return;
        }

        if (!TryReadSeconds(SecondsAfter.Text, out var after))
        {
            ShowInvalid("Seconds after impact must be between 0.1 and 10.");
            return;
        }

        if (!TryReadCount(SwingsToKeep.Text, out var swingsToKeep))
        {
            ShowInvalid("Swings to keep must be a whole number from 1 to 50.");
            return;
        }

        if (CaptureModeList.SelectedItem is not CaptureMode mode)
        {
            ShowInvalid("Choose a capture mode.");
            return;
        }

        string folder;
        try
        {
            folder = Path.GetFullPath(SessionFolder.Text.Trim());
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowInvalid(ex.Message);
            return;
        }

        _settings.TriggerSource = TriggerSources.Normalize(SelectedTrigger());
        _settings.TriggerThreshold = (int)Math.Round(ThresholdSlider.Value);
        _settings.SecondsBeforeImpact = before;
        _settings.SecondsAfterImpact = after;
        _settings.CaptureWidth = mode.Width;
        _settings.CaptureHeight = mode.Height;
        _settings.CaptureFramesPerSecond = mode.FramesPerSecond;
        _settings.CaptureFourCc = mode.FourCc;
        _settings.SessionFolder = folder;
        _settings.SwingsToKeep = swingsToKeep;
        DialogResult = true;
    }

    private void SecondsBeforeUp_Click(object sender, RoutedEventArgs e) => StepSeconds(SecondsBefore, 0.1);

    private void SecondsBeforeDown_Click(object sender, RoutedEventArgs e) => StepSeconds(SecondsBefore, -0.1);

    private void SecondsAfterUp_Click(object sender, RoutedEventArgs e) => StepSeconds(SecondsAfter, 0.1);

    private void SecondsAfterDown_Click(object sender, RoutedEventArgs e) => StepSeconds(SecondsAfter, -0.1);

    private void SwingsToKeepUp_Click(object sender, RoutedEventArgs e) => StepCount(SwingsToKeep, 1);

    private void SwingsToKeepDown_Click(object sender, RoutedEventArgs e) => StepCount(SwingsToKeep, -1);

    private static void StepSeconds(TextBox box, double delta)
    {
        var trimmed = box.Text.Trim();
        var parsed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds);
        if (!parsed)
        {
            seconds = 1;
        }

        seconds = Math.Clamp(Math.Round(seconds + delta, 1, MidpointRounding.AwayFromZero), 0.1, 10);
        box.Text = seconds.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static void StepCount(TextBox box, int delta)
    {
        var trimmed = box.Text.Trim();
        var parsed = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out count);
        if (!parsed)
        {
            count = 10;
        }

        box.Text = Math.Clamp(count + delta, 1, 50).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryReadCount(string text, out int count)
    {
        var trimmed = text.Trim();
        var parsed = int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
            || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out count);
        return parsed && count is >= 1 and <= 50;
    }

    private static bool TryReadSeconds(string text, out double seconds)
    {
        var trimmed = text.Trim();
        var parsed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds);
        return parsed && seconds is >= 0.1 and <= 10;
    }

    private void ShowInvalid(string message)
    {
        MessageBox.Show(this, message, "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
