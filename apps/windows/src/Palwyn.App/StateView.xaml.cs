using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Palwyn.App;

/// <summary>What a page shows instead of content, and why. Kinds keep the icon and tone consistent everywhere.</summary>
public sealed record PageState(string Glyph, string Title, string Body, string? Action = null)
{
    public static PageState Offline(string what) =>
        new("", "Your phone isn't connected", $"{what} show up here when it is. Check that it's on the same Wi-Fi as this PC.");

    public static PageState NeedsPermission(string what, string where) =>
        new("", $"Allow {what} on your phone", $"Open Palwyn on your phone and tap Allow next to {where}.", "Check again");

    public static PageState Failed(string what) =>
        new("", $"Couldn't load {what}", "Your phone didn't answer in time. It may be busy or asleep.", "Try again");

    public static PageState Empty(string glyph, string title, string body) => new(glyph, title, body);
}

public sealed partial class StateView : UserControl
{
    public StateView() => InitializeComponent();

    /// <summary>Raised by the action button ("Try again", "Check again").</summary>
    public event Action? ActionClicked;

    /// <summary>null hides the view.</summary>
    public void Show(PageState? state)
    {
        Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;
        if (state is null) return;
        Icon.Glyph = state.Glyph;
        TitleText.Text = state.Title;
        BodyText.Text = state.Body;
        ActionButton.Content = state.Action;
        ActionButton.Visibility = state.Action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Action_Click(object sender, RoutedEventArgs e) => ActionClicked?.Invoke();
}
