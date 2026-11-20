using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Hoardling.Core;
using Microsoft.Win32;

namespace Hoardling.Windows;

/// <summary>
/// The hover card. Click-through and never focused, so it can't get in the way. Built in code from a
/// <see cref="CardModel"/>; rebuilt only when the model changes while it is visible.
/// </summary>
public sealed class CardWindow : Window
{
    private const double CardWidth = 300;
    private static readonly Brush ClaudeFill = Frozen("#E8743B");
    private static readonly Brush AgFill = Frozen("#378ADD");

    private readonly Border _card;
    private CardModel? _shown;
    private Theme _theme = Theme.Current();

    public CardWindow()
    {
        Title = "Hoardling card";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        _card = new Border { Width = CardWidth, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1) };
        Content = _card;
        SourceInitialized += (_, _) => NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle,
            NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent, NativeMethods.WsExAppWindow);
    }

    public void ShowFor(RectD pet, CardModel model, RectD workArea)
    {
        _theme = Theme.Current();
        _anchor = pet;
        _area = workArea;
        _shown = null;
        Update(model);
        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>Refreshes the content; if it is visible and changed size, keeps it on screen.</summary>
    public void Update(CardModel model)
    {
        if (model == _shown)
        {
            return;
        }
        _shown = model;
        Rebuild(model);
        // Measure first and place before showing: no flash at the old spot, never under the taskbar.
        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        var size = _card.DesiredSize;
        var (x, y) = WindowPlacement.CardPosition(_anchor, size.Width, size.Height, _area);
        Left = x;
        Top = y;
    }

    private RectD _anchor;
    private RectD _area;

    private void Rebuild(CardModel model)
    {
        _card.Background = _theme.Background;
        _card.BorderBrush = _theme.Border;
        var stack = new StackPanel();
        for (var s = 0; s < model.Sections.Count; s++)
        {
            if (s > 0)
            {
                stack.Children.Add(new Border { Height = 1, Background = _theme.Border, Margin = new Thickness(0, 10, 0, 10) });
            }
            // Closed apps have no section, so the colour follows the section, not its position.
            AddSection(stack, model.Sections[s], model.Sections[s].Title == "Claude" ? ClaudeFill : AgFill);
        }
        if (model.Sections.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Background = _theme.Border, Margin = new Thickness(0, 10, 0, 8) });
        }
        stack.Children.Add(Text(model.StatusLine, 11, _theme.Secondary, wrap: true));
        stack.Children.Add(Text(model.UpdatedLine, 11, _theme.Secondary));
        _card.Child = stack;
    }

    public void HideCard()
    {
        _shown = null;
        Hide();
    }

    private void AddSection(StackPanel stack, CardSection section, Brush fill)
    {
        var title = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new System.Windows.Documents.Run(section.Title) { FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = _theme.Primary });
        if (section.Plan is not null)
        {
            title.Inlines.Add(new System.Windows.Documents.Run(" · " + section.Plan) { FontSize = 12, Foreground = _theme.Secondary });
        }
        stack.Children.Add(title);

        foreach (var row in section.Rows)
        {
            // Label shrinks with "…"; the percentage and reset time always stay whole.
            var line = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = Text(row.Label, 12, _theme.Primary);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(label);
            var right = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(right, 1);
            if (row.UsedPercent is { } used)
            {
                right.Inlines.Add(new System.Windows.Documents.Run($"{Math.Round(used)}%") { FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = _theme.Primary });
            }
            if (row.Detail is not null)
            {
                right.Inlines.Add(new System.Windows.Documents.Run(" · " + row.Detail) { FontSize = 11, Foreground = _theme.Secondary });
            }
            line.Children.Add(right);
            stack.Children.Add(line);
            stack.Children.Add(Bar(row.UsedPercent ?? 0, fill));
        }
        if (section.Notice is not null)
        {
            stack.Children.Add(Text(section.Notice, 12, _theme.Secondary, wrap: true, italic: true));
        }
    }

    private Grid Bar(double percent, Brush fill)
    {
        var grid = new Grid { Height = 4, Margin = new Thickness(0, 1, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(percent, 0, 100), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - Math.Clamp(percent, 0, 100), GridUnitType.Star) });
        var track = new Border { Background = _theme.Track, CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        grid.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(2) });
        return grid;
    }

    private static TextBlock Text(string text, double size, Brush color, bool wrap = false, bool italic = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
    };

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private sealed record Theme(Brush Background, Brush Border, Brush Primary, Brush Secondary, Brush Track)
    {
        private static readonly Theme Light = new(Frozen("#F7FFFFFF"), Frozen("#1F000000"), Frozen("#1A1A1A"), Frozen("#5F5F5F"), Frozen("#24000000"));
        private static readonly Theme Dark = new(Frozen("#F2202124"), Frozen("#2EFFFFFF"), Frozen("#EDEDED"), Frozen("#A3A3A3"), Frozen("#2EFFFFFF"));

        public static Theme Current()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
        }
    }
}
