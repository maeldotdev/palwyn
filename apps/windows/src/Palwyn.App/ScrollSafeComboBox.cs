using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Palwyn.App;

/// <summary>
/// A ComboBox that ignores the mouse wheel while closed. A focused WinUI ComboBox otherwise steps through its items
/// when the wheel turns over it, so scrolling a page past it (e.g. right after picking a theme) silently changes the choice.
/// Unhandled, the wheel reaches the page's ScrollViewer and scrolls it instead.
/// </summary>
public sealed class ScrollSafeComboBox : ComboBox
{
    public ScrollSafeComboBox() => DefaultStyleKey = typeof(ComboBox);

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsDropDownOpen) base.OnPointerWheelChanged(e);
    }
}
