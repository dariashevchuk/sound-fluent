using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SoundFluent.Services;

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
    private bool _settingsOpen;
    private UIElement? _busyRow;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Topmost = _settings.AlwaysOnTop;
        ModePicker.SelectedIndex = Math.Clamp(_settings.DefaultMode, 0, 1);
        SendButton.Content = SendLabel;

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

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        InputPlaceholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        SetBusy(true, "");
        ShowBusyIndicator(isFirstTurn ? "Checking your Polish…" : "Thinking…");

        _inFlight?.Cancel();
        _inFlight = new CancellationTokenSource();
        CancellationToken token = _inFlight.Token;

        try
        {
            if (isFirstTurn)
            {
                Register register = (Register)Math.Max(0, RegisterPicker.SelectedIndex);
                Mode mode = CurrentMode;

                if (mode == Mode.Translate)
                {
                    string polish = await _client
                        .TranslateAsync(_settings.ApiKey!, _settings.Model, text, register, token)
                        .ConfigureAwait(true);

                    _history.Add(new ApiTurn("user", $"Translate into Polish:\n\n{text}"));
                    _history.Add(new ApiTurn("assistant", polish));
                    SetLastReply(polish);

                    AddPlainReply(polish, _settings.AutoCopy);
                }
                else
                {
                    Correction result = await _client
                        .CorrectAsync(_settings.ApiKey!, _settings.Model, text, register, token)
                        .ConfigureAwait(true);

                    _history.Add(new ApiTurn("user", $"Correct this Polish text:\n\n{text}"));
                    _history.Add(new ApiTurn("assistant", result.CorrectedText));
                    SetLastReply(result.CorrectedText);

                    AddCorrection(text, result, _settings.AutoCopy);
                }
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
            string status = _lastReply is null
                ? "Ctrl+Alt+P from anywhere"
                : (_settings.AutoCopy ? "Copied to clipboard — Esc hides" : "Ctrl+Enter copies and hides");
            SetBusy(false, status);
        }
    }

    private void SetLastReply(string text)
    {
        _lastReply = text;
        if (_settings.AutoCopy) WriteClipboard(text);
    }

    private Mode CurrentMode => (Mode)Math.Max(0, ModePicker.SelectedIndex);

    private string SendLabel => CurrentMode == Mode.Translate ? "Translate  ⏎" : "Check  ⏎";

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once during InitializeComponent, before the rest of the tree exists.
        if (SendButton is null) return;

        SendButton.Content = SendLabel;
        _settings.DefaultMode = (int)CurrentMode;
        _settings.Save();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        SendButton.Content = busy ? "…" : SendLabel;
        StatusLine.Text = status;
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
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        };
        Append(slip);
    }

    /// <summary>Colors a reply's copy button for its "not yet copied" / "copied" state.</summary>
    private void SetCopyState(Button button, bool copied)
    {
        button.Content = copied ? "Copied ✓" : "Copy";
        if (copied)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = Res("Accent");
            button.BorderBrush = Res("PaperEdge");
        }
        else
        {
            button.Background = Res("Accent");
            button.Foreground = Brushes.White;
            button.BorderBrush = Res("Accent");
        }
    }

    private Button MakeCopyButton(string textToCopy, bool alreadyCopied)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["ReplyCopyButton"],
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0)
        };
        SetCopyState(button, alreadyCopied);
        button.Click += (_, _) =>
        {
            WriteClipboard(textToCopy);
            SetCopyState(button, true);
        };
        return button;
    }

    private void AddCorrection(string original, Correction result, bool autoCopied)
    {
        Border slip = Slip(Brushes.White);
        var stack = new StackPanel();

        var headRow = new Grid();
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var correctedText = new TextBlock
        {
            Text = result.CorrectedText,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 15,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        };
        Grid.SetColumn(correctedText, 0);
        headRow.Children.Add(correctedText);

        Button copyBtn = MakeCopyButton(result.CorrectedText, autoCopied);
        Grid.SetColumn(copyBtn, 1);
        headRow.Children.Add(copyBtn);

        stack.Children.Add(headRow);

        if (result.Errors.Count > 0)
        {
            List<DiffPart> parts = WordDiff.Compute(original, result.CorrectedText);

            var expandBody = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };

            var marks = new TextBlock
            {
                FontFamily = new FontFamily("Cambria"),
                FontSize = 13,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap
            };
            foreach (DiffPart part in parts)
            {
                var run = new Run(part.Word + " ");
                switch (part.Op)
                {
                    case DiffOp.Removed:
                        run.Foreground = Res("MarkRed");
                        run.TextDecorations = TextDecorations.Strikethrough;
                        break;
                    case DiffOp.Added:
                        run.Foreground = Res("MarkBlue");
                        run.FontWeight = FontWeights.Bold;
                        break;
                    default:
                        run.Foreground = Res("InkMuted");
                        break;
                }
                marks.Inlines.Add(run);
            }
            expandBody.Children.Add(marks);

            var errorsPanel = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
            foreach (GrammarError error in result.Errors)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) };
                line.Inlines.Add(new Run(error.Original)
                {
                    FontFamily = new FontFamily("Cambria"),
                    FontSize = 13,
                    Foreground = Res("MarkRed"),
                    TextDecorations = TextDecorations.Strikethrough
                });
                line.Inlines.Add(new Run("  →  ") { FontFamily = new FontFamily("Segoe UI"), Foreground = Res("InkMuted") });
                line.Inlines.Add(new Run(error.Fixed)
                {
                    FontFamily = new FontFamily("Cambria"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Res("MarkBlue")
                });
                line.Inlines.Add(new LineBreak());
                line.Inlines.Add(new Run(error.Rule)
                {
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = Res("InkMuted")
                });
                errorsPanel.Children.Add(line);
            }
            expandBody.Children.Add(errorsPanel);

            int fixCount = result.Errors.Count;
            var badge = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                Background = Res("MarkRed"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = fixCount.ToString(),
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var toggleLabel = new TextBlock
            {
                Text = fixCount == 1 ? "fix" : "fixes",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("MarkBlue"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var toggleInner = new StackPanel { Orientation = Orientation.Horizontal };
            toggleInner.Children.Add(badge);
            toggleInner.Children.Add(toggleLabel);

            var toggleBtn = new Button { Style = (Style)Application.Current.Resources["QuietFlatButton"], Content = toggleInner };

            string hintText = string.Join(", ", result.Errors.Select(ShortRule).Distinct().Take(2));
            var hint = new TextBlock
            {
                Text = hintText,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = Res("InkMuted"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var toggleRowInner = new StackPanel { Orientation = Orientation.Horizontal };
            toggleRowInner.Children.Add(toggleBtn);
            toggleRowInner.Children.Add(hint);

            var toggleRow = new Border
            {
                BorderBrush = Res("PaperEdge"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 11, 0, 0),
                Padding = new Thickness(0, 10, 0, 0),
                Child = toggleRowInner
            };

            toggleBtn.Click += (_, _) =>
            {
                bool willExpand = expandBody.Visibility != Visibility.Visible;
                expandBody.Visibility = willExpand ? Visibility.Visible : Visibility.Collapsed;
                toggleLabel.Text = willExpand ? "Hide the marks" : (fixCount == 1 ? "fix" : "fixes");
                hint.Visibility = willExpand ? Visibility.Collapsed : Visibility.Visible;
            };

            stack.Children.Add(toggleRow);
            stack.Children.Add(expandBody);
        }
        else
        {
            stack.Children.Add(Divider());
            stack.Children.Add(new TextBlock
            {
                Text = "No errors found.",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = Res("InkMuted")
            });
        }

        slip.Child = stack;
        Append(slip);
    }

    /// <summary>Trims a full explanation like "genitive after 'do', not nominative" down to its concept.</summary>
    private static string ShortRule(GrammarError error)
    {
        int comma = error.Rule.IndexOf(',');
        return (comma > 0 ? error.Rule[..comma] : error.Rule).Trim();
    }

    private void AddPlainReply(string text, bool autoCopied)
    {
        Border slip = Slip(Brushes.White);
        var stack = new StackPanel();

        var headRow = new Grid();
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var body = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 15,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        };
        Grid.SetColumn(body, 0);
        headRow.Children.Add(body);

        Button copyBtn = MakeCopyButton(text, autoCopied);
        Grid.SetColumn(copyBtn, 1);
        headRow.Children.Add(copyBtn);

        stack.Children.Add(headRow);
        slip.Child = stack;
        Append(slip);
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
