using System.Windows;

namespace SwingStudio;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SessionsPathText.Text = AppPaths.SessionsRoot;
    }
}
