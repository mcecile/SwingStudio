using System.Windows;

namespace SwingStudio;

public partial class NameSwingWindow : Window
{
    public NameSwingWindow(string suggestedName)
    {
        InitializeComponent();
        SwingNameBox.Text = suggestedName;
    }

    public string SwingName => SwingNameBox.Text.Trim();

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SwingNameBox.SelectAll();
        SwingNameBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SwingNameBox.Text))
        {
            MessageBox.Show(this, "Enter a name.", "Save Swing", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
