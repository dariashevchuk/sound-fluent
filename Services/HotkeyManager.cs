using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SoundFluent.Services;

/// <summary>
/// Registers a system-wide shortcut with Windows and raises <see cref="Pressed"/>
/// when it fires. Uses RegisterHotKey rather than a keyboard hook, so it works
/// regardless of which application has focus and costs nothing when idle.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int HotkeyId = 0xB0B;

    public const uint VkP = 0x50;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd)
                  ?? throw new InvalidOperationException("Window handle has no HwndSource yet.");
        _source.AddHook(Hook);
    }

    public bool Register(uint virtualKey)
    {
        _registered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, virtualKey);
        return _registered;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        if (_registered) UnregisterHotKey(_hwnd, HotkeyId);
    }
}
