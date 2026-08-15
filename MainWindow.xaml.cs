using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

    public MainWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Topmost = _settings.AlwaysOnTop;
        ModePicker.SelectedIndex = Math.Clamp(_settings.DefaultMode, 0, 1);
        SendButton.Content = SendLabel;
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
        SetBusy(true, isFirstTurn ? "Checking…" : "Thinking…");

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
                    _lastReply = polish;

                    AddPlainReply(polish);
                }
                else
                {
                    Correction result = await _client
                        .CorrectAsync(_settings.ApiKey!, _settings.Model, text, register, token)
                        .ConfigureAwait(true);

                    _history.Add(new ApiTurn("user", $"Correct this Polish text:\n\n{text}"));
                    _history.Add(new ApiTurn("assistant", result.CorrectedText));
                    _lastReply = result.CorrectedText;

                    AddCorrection(text, result);
                }
            }
            else
            {
                _history.Add(new ApiTurn("user", text));

                string reply = await _client
                    .FollowUpAsync(_settings.ApiKey!, _settings.Model, _history, token)
                    .ConfigureAwait(true);

                _history.Add(new ApiTurn("assistant", reply));
                _lastReply = reply;

                AddPlainReply(reply);
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
            SetBusy(false, _lastReply is null ? "" : "Ctrl+Enter copies the last reply and hides");
        }
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
            Text = "Copy something, press Ctrl+Alt+P.\n\n"
                 + "The clipboard lands here and goes straight out — checked if it's "
                 + "Polish, translated if it isn't. After that just keep typing: "
                 + "\"krócej\", \"bardziej formalnie\", \"why that ending?\". "
                 + "The conversation is kept.",
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

    private Border Slip(Brush background, Brush border)
    {
        return new Border
        {
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10)
        };
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

    private void AddUserMessage(string text)
    {
        Border slip = Slip(Res("UserSlip"), Res("PaperEdge"));
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

    private void AddCorrection(string original, Correction result)
    {
        Border slip = Slip(Brushes.White, Res("PaperEdge"));
        var stack = new StackPanel();

        // 1. The corrected text — the thing you came for.
        stack.Children.Add(new TextBlock
        {
            Text = result.CorrectedText,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 15,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        });

        var copy = new Button
        {
            Content = "Copy",
            Style = (Style)Application.Current.Resources["QuietButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0)
        };
        copy.Click += (_, _) =>
        {
            WriteClipboard(result.CorrectedText);
            copy.Content = "Copied";
        };
        stack.Children.Add(copy);

        // 2. Proofreader's marks: struck in red, inserted in blue.
        List<DiffPart> parts = WordDiff.Compute(original, result.CorrectedText);
        bool anyChange = parts.Exists(p => p.Op != DiffOp.Same);

        if (anyChange)
        {
            stack.Children.Add(Divider());

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
            stack.Children.Add(marks);
        }

        // 3. What each change was about.
        if (result.Errors.Count > 0)
        {
            stack.Children.Add(Divider());

            foreach (GrammarError error in result.Errors)
            {
                var line = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12
                };

                line.Inlines.Add(new Run(error.Original)
                {
                    FontFamily = new FontFamily("Cambria"),
                    FontSize = 13,
                    Foreground = Res("MarkRed"),
                    TextDecorations = TextDecorations.Strikethrough
                });
                line.Inlines.Add(new Run("  →  ") { Foreground = Res("InkMuted") });
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
                    FontSize = 11,
                    Foreground = Res("InkMuted")
                });

                stack.Children.Add(line);
            }
        }
        else if (!anyChange)
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

    private void AddPlainReply(string text)
    {
        Border slip = Slip(Brushes.White, Res("PaperEdge"));
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cambria"),
            FontSize = 14,
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("Ink")
        });

        var copy = new Button
        {
            Content = "Copy",
            Style = (Style)Application.Current.Resources["QuietButton"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0)
        };
        copy.Click += (_, _) =>
        {
            WriteClipboard(text);
            copy.Content = "Copied";
        };
        stack.Children.Add(copy);

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
        Margin = new Thickness(0, 11, 0, 11)
    };
}
