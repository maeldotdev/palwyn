using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;

namespace Palwyn.App;

/// <summary>
/// Placeholder shaped like the content that's loading: list rows (avatar + two lines) or photo tiles.
/// A slow pulse says "working"; still when Windows animations are off.
/// </summary>
public sealed partial class Skeleton : UserControl
{
    static readonly UISettings Ui = new();
    readonly Storyboard _pulse = new() { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };

    /// <summary>Photo tiles instead of list rows.</summary>
    public bool Tiles { get; set; }
    public int Count { get; set; } = 6;

    public Skeleton()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { Content = Tiles ? BuildTiles() : BuildRows(); Start(); };
        Unloaded += (_, _) => _pulse.Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) Start(); else _pulse.Stop(); });
    }

    void Start()
    {
        if (!IsLoaded || !Ui.AnimationsEnabled) return;
        _pulse.Stop();
        _pulse.Children.Clear();
        var fade = new DoubleAnimation { From = 1, To = 0.45, Duration = TimeSpan.FromMilliseconds(900) };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _pulse.Children.Add(fade);
        _pulse.Begin();
    }

    static Brush Fill => (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];

    static Rectangle Bar(double width, double height = 12) =>
        new() { Width = width, Height = height, RadiusX = 4, RadiusY = 4, Fill = Fill, HorizontalAlignment = HorizontalAlignment.Left };

    UIElement BuildRows()
    {
        var panel = new StackPanel { Spacing = 20, Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < Count; i++)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Ellipse { Width = 36, Height = 36, Fill = Fill });
            // Varied widths read as text, identical ones as a table.
            var lines = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(Bar(120 + (i * 37) % 90));
            lines.Children.Add(Bar(180 + (i * 53) % 120, 10));
            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);
            panel.Children.Add(row);
        }
        return panel;
    }

    UIElement BuildTiles()
    {
        var grid = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 154, ItemHeight = 154 };
        for (int i = 0; i < Count; i++)
            grid.Children.Add(new Rectangle { Margin = new Thickness(2), RadiusX = 4, RadiusY = 4, Fill = Fill });
        return grid;
    }
}
