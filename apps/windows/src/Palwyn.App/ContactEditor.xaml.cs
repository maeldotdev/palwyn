using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Palwyn.Core;
using Palwyn.Core.Link;

namespace Palwyn.App;

/// <summary>Add or edit a contact. Saving writes it to the phone; the dialog stays open if that fails.</summary>
public sealed partial class ContactEditor : UserControl
{
    const int MaxFields = 20; // the protocol's limit per contact
    readonly ObservableCollection<NumberEdit> _numbers = [];
    readonly ObservableCollection<EmailEdit> _emails = [];

    ContactEditor(PhoneContact? contact, string? number)
    {
        InitializeComponent();
        NumberList.ItemsSource = _numbers;
        EmailList.ItemsSource = _emails;
        NameBox.Text = contact?.Name ?? "";
        foreach (var n in contact?.Numbers ?? []) _numbers.Add(new NumberEdit(n));
        foreach (var e in contact?.Emails ?? []) _emails.Add(new EmailEdit { Address = e });
        if (number is not null) _numbers.Add(new NumberEdit(new ContactNumber(number, "mobile")));
        if (_numbers.Count == 0) _numbers.Add(new NumberEdit(new ContactNumber("", "mobile")));
        UpdateAddButtons();
        Loaded += (_, _) => NameBox.Focus(FocusState.Programmatic);
    }

    /// <summary>A new contact when <paramref name="contact"/> is null, <paramref name="number"/> filled in. The saved contact's id, or null.</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, PhoneContact? contact, string? number = null)
    {
        var editor = new ContactEditor(contact, number);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
            Title = contact is null ? "New contact" : "Edit contact",
            Content = new ScrollViewer { Content = editor, MaxHeight = 480, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            args.Cancel = !await editor.SaveAsync(contact?.Id ?? "");
            dialog.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? editor._savedId : null;
    }

    string? _savedId;

    async Task<bool> SaveAsync(string id)
    {
        var numbers = _numbers.Where(n => n.Number.Trim().Length > 0).Select(n => n.ToNumber()).ToList();
        var emails = _emails.Select(e => e.Address.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = numbers.FirstOrDefault()?.Number ?? emails.FirstOrDefault() ?? "";
        if (name.Length == 0) return Fail("Add a name or a number.");
        if (emails.FirstOrDefault(e => !e.Contains('@')) is { } bad) return Fail($"\"{bad}\" isn't an email address.");
        try
        {
            _savedId = await ContactBook.SaveAsync(new PhoneContact(id, name, numbers, emails));
            return true;
        }
        catch (Exception e) when (e is PhoneErrorException or TimeoutException or IOException or InvalidOperationException)
        {
            Log.Info($"Contact save failed: {e.Message}");
            return Fail("Couldn't save to your phone. " + ContactBook.Problem(e));
        }
    }

    bool Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    void UpdateAddButtons()
    {
        AddNumberButton.Visibility = _numbers.Count < MaxFields ? Visibility.Visible : Visibility.Collapsed;
        AddEmailButton.Visibility = _emails.Count < MaxFields ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddNumber_Click(object sender, RoutedEventArgs e)
    {
        _numbers.Add(new NumberEdit(new ContactNumber("", "mobile")));
        UpdateAddButtons();
    }

    void AddEmail_Click(object sender, RoutedEventArgs e)
    {
        _emails.Add(new EmailEdit());
        UpdateAddButtons();
    }

    void RemoveNumber_Click(object sender, RoutedEventArgs e)
    {
        _numbers.Remove((NumberEdit)((FrameworkElement)sender).DataContext);
        UpdateAddButtons();
    }

    void RemoveEmail_Click(object sender, RoutedEventArgs e)
    {
        _emails.Remove((EmailEdit)((FrameworkElement)sender).DataContext);
        UpdateAddButtons();
    }
}

public sealed class NumberEdit(ContactNumber n)
{
    static readonly string[] Keys = ["mobile", "home", "work", "other"];

    public string Number { get; set; } = n.Number;
    public int TypeIndex { get; set; } = Math.Max(0, Array.IndexOf(Keys, n.Type));
    /// <summary>A custom label ("Gym") takes the place of "Other", so editing keeps it.</summary>
    public List<string> Types { get; } = ["Mobile", "Home", "Work", n.Type == "other" && n.Label is { Length: > 0 } l ? l : "Other"];

    public ContactNumber ToNumber() => new(Number.Trim(), Keys[TypeIndex is >= 0 and < 4 ? TypeIndex : 0], TypeIndex == 3 ? Types[3] : null);
}

public sealed class EmailEdit
{
    public string Address { get; set; } = "";
}
