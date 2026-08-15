using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundFluent.Services;

namespace SoundFluent.Ui;

/// <summary>Small modal for entering the OpenAI key. Built in code; too simple for XAML.</summary>
public static class ApiKeyDialog
{
    public static bool Prompt(Settings settings)
    {
        var window = new Window
        {
            Title = "Sound Fluent — API key",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true,
            Topmost = true,
            Background = (Brush)Application.Current.Resources["Paper"]
        };

        var stack = new StackPanel { Margin = new Thickness(20) };

        stack.Children.Add(new TextBlock
        {
            Text = "Paste your OpenAI API key",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["Ink"],
            Margin = new Thickness(0, 0, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = settings.ApiKeyIsFromEnvironment
                ? $"{Settings.ApiKeyVariable} is set, and it takes priority. Anything "
                  + "saved here is only used if that variable goes away."
                : "Stored encrypted to your Windows account, in "
                  + "%APPDATA%\\SoundFluent\\settings.json. Setting the "
                  + $"{Settings.ApiKeyVariable} environment variable overrides it.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["InkMuted"],
            Margin = new Thickness(0, 0, 0, 12)
        });

        var box = new TextBox
        {
            Text = settings.ApiKeyIsFromEnvironment ? "" : settings.ApiKey ?? "",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brushes.White,
            BorderBrush = (Brush)Application.Current.Resources["PaperEdge"],
            BorderThickness = new Thickness(1)
        };
        stack.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.Resources["QuietButton"],
            Margin = new Thickness(0, 0, 8, 0)
        };

        var save = new Button
        {
            Content = "Save key",
            Style = (Style)Application.Current.Resources["PrimaryButton"],
            IsDefault = true
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        window.Content = stack;

        bool saved = false;
        save.Click += (_, _) =>
        {
            settings.StoreApiKey(box.Text);
            saved = true;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();

        return saved;
    }
}
