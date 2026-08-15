using System;
using System.Windows;
using System.Windows.Interop;
using SoundFluent.Services;
using SoundFluent.Ui;

namespace SoundFluent;

public partial class App : Application
{
    private MainWindow? _window;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var settings = Settings.Load();

        _window = new MainWindow(settings);

        // Force the HWND into existence without showing the window, so the
        // global hotkey can be attached to it while we stay hidden in the tray.
        var helper = new WindowInteropHelper(_window);
        helper.EnsureHandle();

        _hotkeys = new HotkeyManager(helper.Handle);
        _hotkeys.Pressed += () => _window!.ShowFromHotkey();

        if (!_hotkeys.Register(HotkeyManager.VkP))
        {
            MessageBox.Show(
                "Ctrl+Alt+P is already taken by another program, so the global shortcut is off. " +
                "Open the window from the tray icon instead.",
                "Sound Fluent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _tray = new TrayIcon();
        _tray.OpenRequested += () => _window!.ShowFromHotkey();
        _tray.ApiKeyRequested += () =>
        {
            if (ApiKeyDialog.Prompt(settings)) settings.Save();
        };
        _tray.ExitRequested += () => Shutdown();

        // Only prompt when nothing resolves: no OPENAI_API_KEY, no stored key.
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            if (ApiKeyDialog.Prompt(settings)) settings.Save();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
