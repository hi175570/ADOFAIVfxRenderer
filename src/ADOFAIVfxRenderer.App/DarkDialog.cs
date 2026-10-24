using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ADOFAIVfxRenderer.App;

internal static class DarkDialog
{
    public static void Show(Window owner, string message, string title)
    {
        var ok = new Button
        {
            Content = "确定",
            MinWidth = 88,
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            MaxWidth = 520,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
        });
        Grid.SetRow(ok, 2);
        panel.Children.Add(ok);

        var dialog = new Window
        {
            Owner = owner,
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 340,
            MaxWidth = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.Resources["WindowBackground"],
            Foreground = (Brush)Application.Current.Resources["TextPrimary"],
        };
        dialog.SourceInitialized += (_, _) => ApplyDarkCaption(dialog);
        ok.Click += (_, _) => dialog.DialogResult = true;
        _ = dialog.ShowDialog();
    }

    private static void ApplyDarkCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        var captionColor = 0x001A1411;
        var textColor = 0x00FAF5F2;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
