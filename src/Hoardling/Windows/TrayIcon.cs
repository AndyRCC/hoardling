using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using static Hoardling.Windows.NativeMethods;

namespace Hoardling.Windows;

/// <summary>
/// Notification-area icon via Shell_NotifyIcon (no WinForms: saves memory). Left or right click opens a
/// WPF context menu. Re-adds itself when Explorer restarts (TaskbarCreated).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = WmApp + 1;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _icon;
    private readonly Window _owner;
    private readonly ContextMenu _menu;
    private readonly MenuItem _toggleItem;
    private readonly MenuItem _startupItem;
    private readonly MenuItem _hooksItem;
    private NotifyIconData _data;
    private bool _disposed;

    public event Action? ToggleRequested;
    public event Action<bool>? StartupToggled;
    public event Action<bool>? HooksToggled;
    public event Action? ExitRequested;

    public TrayIcon(Window owner, byte[] iconPng, string tooltip)
    {
        _owner = owner;
        _icon = CreateIconFromResourceEx(iconPng, (uint)iconPng.Length, true, 0x00030000, 32, 32, 0);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _source = new HwndSource(new HwndSourceParameters("HoardlingTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);

        _toggleItem = new MenuItem { Header = "Ocultar dragón" };
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();
        _startupItem = new MenuItem { Header = "Iniciar con Windows", IsCheckable = true };
        _startupItem.Click += (_, _) => StartupToggled?.Invoke(_startupItem.IsChecked);
        _hooksItem = new MenuItem { Header = "Avisos de Claude Code (hooks)", IsCheckable = true };
        _hooksItem.Click += (_, _) => HooksToggled?.Invoke(_hooksItem.IsChecked);
        var exit = new MenuItem { Header = "Salir" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint, StaysOpen = false };
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(_startupItem);
        _menu.Items.Add(_hooksItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);

        _data = new NotifyIconData
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = Trim(tooltip),
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIcon(NimAdd, ref _data);
    }

    public bool StartupChecked { set => _startupItem.IsChecked = value; }

    public bool HooksChecked { set => _hooksItem.IsChecked = value; }

    public bool PetVisible { set => _toggleItem.Header = value ? "Ocultar dragón" : "Mostrar dragón"; }

    public string Tooltip
    {
        set
        {
            var text = Trim(value);
            if (_disposed || text == _data.szTip)
            {
                return;
            }
            _data.szTip = text;
            Shell_NotifyIcon(NimModify, ref _data);
        }
    }

    private static string Trim(string s) => s.Length > 127 ? s[..127] : s;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var mouse = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouse is WmLButtonUp or WmRButtonUp or WmContextMenu)
            {
                ShowMenu();
                handled = true;
            }
        }
        else if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            Shell_NotifyIcon(NimAdd, ref _data);
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        // The menu only closes on outside clicks if our process owns the foreground.
        var helper = new WindowInteropHelper(_owner);
        SetForegroundWindow(_owner.IsVisible && helper.Handle != IntPtr.Zero ? helper.Handle : _source.Handle);
        _menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Shell_NotifyIcon(NimDelete, ref _data);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }
    }
}
