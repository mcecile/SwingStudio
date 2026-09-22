using System.Windows;
using System.Windows.Controls;
using SwingStudio.Capture;
using SwingStudio.Settings;

namespace SwingStudio;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<CameraDevice> _cameras;

    public SettingsWindow(AppSettings settings, IReadOnlyList<CameraDevice> cameras)
    {
        _settings = settings;
        _cameras = cameras;
        InitializeComponent();
        Fill(CameraAList, settings.CameraADevicePath);
        Fill(CameraBList, settings.CameraBDevicePath);
    }

    private void Fill(ComboBox list, string? selectedPath)
    {
        list.Items.Add(new CameraChoice(null, "(None)"));
        foreach (var camera in _cameras)
        {
            list.Items.Add(new CameraChoice(camera.DevicePath, camera.Name));
        }

        list.SelectedItem = list.Items.Cast<CameraChoice>().FirstOrDefault(choice => choice.DevicePath == selectedPath)
            ?? list.Items[0];
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var cameraA = (CameraChoice)CameraAList.SelectedItem;
        var cameraB = (CameraChoice)CameraBList.SelectedItem;
        if (cameraA.DevicePath is not null && cameraA.DevicePath == cameraB.DevicePath)
        {
            MessageBox.Show(this, "Choose a different camera for each pane.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.CameraADevicePath = cameraA.DevicePath;
        _settings.CameraAFriendlyName = cameraA.DevicePath is null ? null : cameraA.Name;
        _settings.CameraBDevicePath = cameraB.DevicePath;
        _settings.CameraBFriendlyName = cameraB.DevicePath is null ? null : cameraB.Name;
        DialogResult = true;
    }

    private sealed record CameraChoice(string? DevicePath, string Name)
    {
        public override string ToString() => Name;
    }
}
