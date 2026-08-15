using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoundFluent.Services;
using SoundFluent.Ui;

namespace SoundFluent;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly OpenAiClient _client = new();

    /// <summary>Turns sent back to the API as context. Excludes system prompts.</summary>
    private readonly List<ApiTurn> _history = new();

    /// <summary>The clipboard text this thread started from, so we know when it's stale.</summary>
    private string? _threadSource;

    /// <summary>Most recent assistant text, for the copy-and-close shortcut.</summary>
    private string? _lastReply;

    private CancellationTokenSource? _inFlight;
    private bool _busy;
    private bool _placeholderShowing;
    private bool _inputResizePending;
    private bool _settingsOpen;
    private UIElement? _busyRow;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Topmost = _settings.AlwaysOnTop;

        ModelPicker.Text = _settings.Model;
        AutoCopyToggle.IsChecked = _settings.AutoCopy;
        AutoSendToggle.IsChecked = _settings.AutoSend;
        OnTopToggle.IsChecked = _settings.AlwaysOnTop;

        ShowPlaceholder();
    }

    // ---------------------------------------------------------------- showing

    /// <summary>
    /// Entry point from the global hotkey and the tray. New clipboard text starts
    /// a fresh thread; the same text just brings the existing conversation back up,
    /// so "shorter" still has something to be shorter than.
    /// </summary>
    public void ShowFromHotkey()
    {
        string clip = ReadClipboard();
        bool isNew = !string.IsNullOrWhiteSpace(clip) && clip != _threadSource;

        Show();
        WindowState = WindowState.Normal;
        Activate();

        if (isNew) StartThread(clip);
        else InputBox.Focus();
    }

    private void StartThread(string text)
    {
        _inFlight?.Cancel();
        _history.Clear();
        _lastReply = null;
        _threadSource = text;
        Messages.Children.Clear();
        _placeholderShowing = false;
        SetSettingsOpen(false);

        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();

        if (_settings.AutoSend)
        {
            // Let the window finish showing before firing, so the transcript scrolls right.
            Dispatcher.BeginInvoke(new Action(() => _ = SendAsync()), DispatcherPriority.Background);
        }
    }

    private static string ReadClipboard()
    {
        // The clipboard is a shared OS resource and another process can hold it open.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (Exception)
            {
                Thread.Sleep(40);
            }
        }
        return "";
    }

    private static void WriteClipboard(string text)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(40);
            }
        }
    }

    // --------------------------------------------------------------- chrome

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { /* button released mid-call */ }
    }

    private void OnSettingsGearClick(object sender, RoutedEventArgs e) => SetSettingsOpen(!_settingsOpen);

    private void OnSettingsDoneClick(object sender, RoutedEventArgs e) => SetSettingsOpen(false);

    private void SetSettingsOpen(bool open)
    {
        _settingsOpen = open;
        SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ChatPanel.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        if (!open) InputBox.Focus();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideWindow();

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string edge } || WindowState != WindowState.Normal)
            return;

        double width = ActualWidth;
        double height = ActualHeight;

        if (edge.Contains("Left", StringComparison.Ordinal))
        {
            double newWidth = Math.Clamp(width - e.HorizontalChange, MinWidth, MaxWidth);
            Left += width - newWidth;
            Width = newWidth;
        }
        else if (edge.Contains("Right", StringComparison.Ordinal))
        {
            Width = Math.Clamp(width + e.HorizontalChange, MinWidth, MaxWidth);
        }

        if (edge.Contains("Top", StringComparison.Ordinal))
        {
            double newHeight = Math.Clamp(height - e.VerticalChange, MinHeight, MaxHeight);
            Top += height - newHeight;
            Height = newHeight;
        }
        else if (edge.Contains("Bottom", StringComparison.Ordinal))
        {
            Height = Math.Clamp(height + e.VerticalChange, MinHeight, MaxHeight);
        }
    }

    // ------------------------------------------------------------- settings

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelPicker.SelectedItem is ComboBoxItem item)
            CommitModel(item.Content?.ToString());
    }

    private void OnModelLostFocus(object sender, RoutedEventArgs e) => CommitModel(ModelPicker.Text);

    private void CommitModel(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0 || text == _settings.Model) return;
        _settings.Model = text;
        _settings.Save();
    }

    private void OnAutoCopyToggled(object sender, RoutedEventArgs e)
    {
        _settings.AutoCopy = AutoCopyToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnAutoSendToggled(object sender, RoutedEventArgs e)
    {
        _settings.AutoSend = AutoSendToggle.IsChecked == true;
        _settings.Save();
    }

    private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = OnTopToggle.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
        _settings.Save();
    }

    // ----------------------------------------------------------------- input

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || Keyboard.Modifiers != ModifierKeys.None)
            return;

        ScrollViewer target;

        if (_settingsOpen)
        {
            // Keep arrows available for choosing a model in the editable combo.
            if (ModelPicker.IsKeyboardFocusWithin)
                return;

            target = SettingsPanel;
        }
        else
        {
            if (InputBox.IsKeyboardFocusWithin)
            {
                int caretLine = InputBox.GetLineIndexFromCharacterIndex(InputBox.CaretIndex);
                bool canMoveCaret = e.Key == Key.Up
                    ? caretLine > 0
                    : caretLine >= 0 && caretLine < InputBox.LineCount - 1;

                if (canMoveCaret)
                    return;
            }

            target = Transcript;
        }

        double distance = e.Key == Key.Down ? 32 : -32;
        e.Handled = SmoothScroll.ScrollBy(target, distance);
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (e.Key == Key.Escape)
        {
            HideWindow();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return)
        {
            if (ctrl)
            {
                CopyLatestAndHide();
                e.Handled = true;
            }
            else if (!shift)
            {
                _ = SendAsync();
                e.Handled = true;
            }
            // Shift+Enter falls through to insert a newline.
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => ScheduleInputResize();

    private void OnInputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            ScheduleInputResize();
    }

    private void ScheduleInputResize()
    {
        if (_inputResizePending) return;
        _inputResizePending = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _inputResizePending = false;
            InputBox.UpdateLayout();

            const double lineHeight = 23;
            const double paddingAndBorder = 15;
            double contentHeight = paddingAndBorder + Math.Max(1, InputBox.LineCount) * lineHeight;
            InputBox.Height = Math.Min(InputBox.MaxHeight, contentHeight);
        }), DispatcherPriority.Background);
    }

    private void CopyLatestAndHide()
    {
        if (string.IsNullOrEmpty(_lastReply)) return;
        WriteClipboard(_lastReply);
        HideWindow();
    }

    private void HideWindow()
    {
        _inFlight?.Cancel();
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The X button parks the app in the tray. Quit is on the tray menu.
        e.Cancel = true;
        HideWindow();
    }

    // ------------------------------------------------------------------ send

    private async Task SendAsync()
    {
        if (_busy) return;

        string text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            AddNotice("No API key set. Right-click the tray icon and choose \"Set API key…\".");
            return;
        }

        bool isFirstTurn = _history.Count == 0;

        AddUserMessage(text);
        InputBox.Clear();
        SetBusy(true);
        ShowBusyIndicator(isFirstTurn ? "Writing in Polish…" : "Thinking…");

        _inFlight?.Cancel();
        _inFlight = new CancellationTokenSource();
        CancellationToken token = _inFlight.Token;

        try
        {
            if (isFirstTurn)
            {
                string result = await _client
                    .PolishAsync(_settings.ApiKey!, _settings.Model, text, token)
                    .ConfigureAwait(true);

                _history.Add(new ApiTurn("user", text));
                _history.Add(new ApiTurn("assistant", result));
                SetLastReply(result);
                AddPlainReply(result, _settings.AutoCopy);
            }
            else
            {
                _history.Add(new ApiTurn("user", text));

                string reply = await _client
                    .FollowUpAsync(_settings.ApiKey!, _settings.Model, _history, token)
                    .ConfigureAwait(true);

                _history.Add(new ApiTurn("assistant", reply));
                SetLastReply(reply);

                AddPlainReply(reply, _settings.AutoCopy);
            }
        }
        catch (OperationCanceledException)
        {
            // Window was hidden or a new thread started; nothing to report.
        }
        catch (ApiException ex)
        {
            AddNotice(ex.Message);
        }
        catch (Exception ex)
        {
            AddNotice($"Request failed: {ex.Message}");
        }
        finally
        {
            HideBusyIndicator();
            SetBusy(false);
        }
    }

    private void SetLastReply(string text)
    {
        _lastReply = text;
        if (_settings.AutoCopy) WriteClipboard(text);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
    }

    // ------------------------------------------------------------- rendering

    private Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void ShowPlaceholder()
    {
        var hint = new TextBlock
        {
            Text = "Copy something, press Ctrl+Alt+P. The clipboard lands here and goes straight out — "
                 + "checked if it's Polish, translated if it isn't. After that just keep typing: "
                 + "\"krócej\", \"bardziej formalnie\", \"why that ending?\"",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("InkMuted"),
            Margin = new Thickness(2, 8, 2, 0)
        };
        Messages.Children.Add(hint);
        _placeholderShowing = true;
    }

    private Border Slip(Brush background, Brush? border = null)
    {
        var slip = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.08, BlurRadius = 6, ShadowDepth = 1, Direction = 270 }
        };

        if (border is not null)
        {
            slip.BorderBrush = border;
            slip.BorderThickness = new Thickness(1);
        }

        return slip;
    }

    private void Append(UIElement element)
    {
        if (_placeholderShowing)
        {
            Messages.Children.Clear();
            _placeholderShowing = false;
        }

        Messages.Children.Add(element);
        Transcript.ScrollToEnd();
    }

    private void ShowBusyIndicator(string label)
    {
        HideBusyIndicator();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 4, 2, 8) };
        row.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Res("Accent"),
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = Res("InkMuted")
        });

        _busyRow = row;
        Append(row);
    }

    private void HideBusyIndicator()
    {
        if (_busyRow is null) return;
        Messages.Children.Remove(_busyRow);
        _busyRow = null;
    }

    private void AddUserMessage(string text)
    {
        Border slip = Slip(Res("UserSlip"));
        slip.Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 15,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        };
        Append(slip);
    }

    /// <summary>Styles a reply's copy control for its "not yet copied" / "copied" state.</summary>
    private void SetCopyState(Button button, bool copied)
    {
        button.Content = copied ? "Copied ✓" : "Copy";
        if (copied)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Res("InkMuted");
            button.BorderBrush = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }
        else
        {
            button.Background = Res("Accent");
            button.Foreground = Brushes.White;
            button.BorderBrush = Res("Accent");
            button.BorderThickness = new Thickness(1);
        }
    }

    private Button MakeCopyButton(string textToCopy, bool alreadyCopied)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ReplyCopyButton"],
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };
        SetCopyState(button, alreadyCopied);
        button.Click += (_, _) =>
        {
            WriteClipboard(textToCopy);
            SetCopyState(button, true);
        };
        return button;
    }

    private void AddPlainReply(string text, bool autoCopied)
    {
        Border slip = Slip(Brushes.White);
        slip.Margin = new Thickness(0);

        var body = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 15,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        };
        slip.Child = body;

        var reply = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        reply.Children.Add(slip);
        reply.Children.Add(MakeCopyButton(text, autoCopied));
        Append(reply);
    }

    private void AddNotice(string message)
    {
        Border slip = Slip(Brushes.White, Res("MarkRed"));
        slip.Child = new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("MarkRed")
        };
        Append(slip);
    }

    private Border Divider() => new()
    {
        Height = 1,
        Background = Res("PaperEdge"),
        Opacity = 0.6,
        Margin = new Thickness(0, 11, 0, 11)
    };
}
