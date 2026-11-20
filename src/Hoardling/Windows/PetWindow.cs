using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Hoardling.Core;
using Hoardling.Rendering;

namespace Hoardling.Windows;

/// <summary>Borderless, transparent, always-on-top window that hosts the dragon. Not in taskbar or Alt+Tab.</summary>
public sealed class PetWindow : Window
{
    private readonly DragonView _dragon = new();

    public event Action<double, double>? Moved;
    public event Action? HoverStarted;
    public event Action? HoverEnded;
    public event Action? HiddenByUser;

    /// <summary>Set by the app right before shutting down; otherwise closing only hides (e.g. Alt+F4).</summary>
    public bool AllowClose { get; set; }

    public PetWindow()
    {
        Title = "Hoardling";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = _dragon;
        Cursor = Cursors.Hand;

        SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WsExToolWindow, NativeMethods.WsExAppWindow);
        MouseLeftButtonDown += OnMouseDown;
        MouseEnter += (_, _) => HoverStarted?.Invoke();
        MouseLeave += (_, _) => HoverEnded?.Invoke();
        Closing += (_, e) =>
        {
            if (AllowClose)
            {
                return;
            }
            // A closed WPF window can never be shown again: hide instead, the tray can bring it back.
            e.Cancel = true;
            Hide();
            HiddenByUser?.Invoke();
        };
    }

    public RectD Bounds => new(Left, Top, ActualWidth, ActualHeight);

    public void Apply(PetVisual visual) => _dragon.Visual = visual;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        HoverEnded?.Invoke();
        var (left, top) = (Left, Top);
        try
        {
            DragMove(); // blocks until the button is released
        }
        catch (InvalidOperationException)
        {
            return; // the button was already released (very quick click, pen or touch)
        }
        if (Left != left || Top != top)
        {
            Moved?.Invoke(Left, Top);
        }
    }
}
