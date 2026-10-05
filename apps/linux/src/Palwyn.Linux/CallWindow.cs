using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Palwyn.Core;

namespace Palwyn.Linux;

/// <summary>
/// The call card: who's calling and Answer, Decline or End, as on Windows. The call's audio always stays on the
/// phone. Asks to stay on top; some Wayland desktops (GNOME) ignore that, so the desktop notification with Answer and
/// Decline is the alert that always shows.
/// </summary>
public sealed class CallWindow : Window
{
    readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _state = new() { Opacity = 0.75 };
    readonly TextBlock _error = new() { FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    PhoneCall? _call;

    public CallWindow()
    {
        Title = "Call";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowActivated = false; // don't take the keyboard from what the user is doing
        FontFamily = App.Mono;
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children = { _title, _state, _buttons, _error, new TextBlock { Text = "You talk on the phone.", FontSize = 12, Opacity = 0.6 } },
        };
        _tick.Tick += (_, _) => _state.Text = StateText();
    }

    /// <summary>Shows the call, or closes the card when it's over (null: the link dropped).</summary>
    public void Show(PhoneCall? call)
    {
        _call = call;
        if (call is null)
        {
            Hide();
            return;
        }
        _title.Text = call.Title;
        _state.Text = StateText();
        _error.Text = "";
        _buttons.Children.Clear();
        if (call.State == CallState.Ringing && call.Incoming)
        {
            _buttons.Children.Add(Command("Answer", "CALL_ANSWER", accent: true));
            _buttons.Children.Add(Command("Decline", "CALL_DECLINE"));
        }
        else if (call.State != CallState.Ended) _buttons.Children.Add(Command("End call", "CALL_END"));

        if (call.State == CallState.Ended)
        {
            _tick.Stop();
            DispatcherTimer.RunOnce(() => { if (_call?.State == CallState.Ended) Hide(); }, TimeSpan.FromSeconds(3));
        }
        else _tick.Start();
        if (!IsVisible) Show();
    }

    string StateText() => _call switch
    {
        { State: CallState.Ringing, Incoming: true } => "Incoming call",
        { State: CallState.Ringing } => "Calling…",
        { State: CallState.Active } c => $"On a call · {DateTimeOffset.Now - c.Since:m\\:ss}",
        _ => "Call ended",
    };

    Button Command(string label, string type, bool accent = false)
    {
        var b = new Button { Content = label };
        if (accent) b.Classes.Add("accent");
        b.Click += async (_, _) =>
        {
            if (_call is not { } call) return;
            try { await App.Current.Link.CallCommandAsync(type, call.Id); }
            catch (Exception e)
            {
                Log.Info($"{type} failed: {e.GetType().Name}");
                _error.Text = "The phone didn't respond. Use the phone instead.";
            }
        };
        return b;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = !e.IsProgrammatic; // closing only hides: the next call reuses the card
        if (e.Cancel) Hide();
        base.OnClosing(e);
    }
}
