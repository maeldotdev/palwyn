using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Palwyn.App.Link;
using Palwyn.Core;
using Palwyn.Core.Link;
using QRCoder;
using Windows.Storage.Streams;

namespace Palwyn.App;

public sealed partial class AddPhonePage : Page
{
    readonly ObservableCollection<DiscoveredPhone> _codePhones = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    CancellationTokenSource _cts = new();
    PairingInvite? _invite;
    DateTimeOffset _expiresAt;
    bool _pairing;

    static LinkManager Link => App.Current.Link;

    public AddPhonePage()
    {
        InitializeComponent();
        CodePhones.ItemsSource = _codePhones;
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            Link.Discovery.Found += OnFound;
            Link.Discovery.Lost += OnLost;
            NewInvite();
            foreach (var p in Link.Discovery.Current) OnFound(p);
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            Link.Discovery.Found -= OnFound;
            Link.Discovery.Lost -= OnLost;
            _timer.Stop();
            _cts.Cancel();
        };
    }

    void NewInvite()
    {
        _invite = Link.NewInvite();
        _expiresAt = DateTimeOffset.UtcNow + PcPairing.InviteLifetime;
        QrImage.Source = Qr(_invite.ToUri());
        SetStatus("Waiting for your phone…", busy: false);
    }

    void Tick()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero && !_pairing) NewInvite(); // one-time secrets expire; show a fresh one
        else ExpiryText.Text = $"This code works once and changes in {left:m\\:ss}.";
    }

    void OnFound(DiscoveredPhone phone) => DispatcherQueue.TryEnqueue(() =>
    {
        _codePhones.Remove(_codePhones.FirstOrDefault(p => p.Key == phone.Key)!);
        if (phone.PairMode == "code") _codePhones.Add(phone);
        NoPhonesText.Visibility = _codePhones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (phone.PairMode == Link.PairingTag && !_pairing && _invite is { } invite) _ = PairQr(phone, invite);
    });

    void OnLost(string key) => DispatcherQueue.TryEnqueue(() =>
    {
        _codePhones.Remove(_codePhones.FirstOrDefault(p => p.Key == key)!);
        NoPhonesText.Visibility = _codePhones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    });

    async Task PairQr(DiscoveredPhone phone, PairingInvite invite)
    {
        await Pair(phone, ct => Link.PairWithQrAsync(phone, invite, _expiresAt, ct));
    }

    async void CodePhone_Click(object sender, RoutedEventArgs e)
    {
        if (_pairing || ((FrameworkElement)sender).Tag is not DiscoveredPhone phone) return;
        await Pair(phone, ct => Link.PairWithCodeAsync(phone, ConfirmCode, ct));
    }

    async Task Pair(DiscoveredPhone phone, Func<CancellationToken, Task<PairedPhone>> pair)
    {
        _pairing = true;
        SetStatus($"Pairing with {phone.Name ?? "your phone"}…", busy: true);
        try
        {
            var paired = await pair(_cts.Token);
            SetStatus($"Paired with {paired.Name}", busy: false);
            App.Current.ShowMain("home");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Pairing failed: {ex.GetType().Name}: {ex.Message}");
            NewInvite(); // a failed attempt burns the one-time secret
            SetStatus(ex is PairingException ? ex.Message : "Couldn't reach the phone. Check that both are on the same Wi-Fi.", busy: false);
        }
        finally { _pairing = false; }
    }

    async Task<bool> ConfirmCode(string code, CancellationToken ct)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Check the code",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{code[..3]} {code[3..]}",
                        FontSize = 40,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        CharacterSpacing = 60,
                    },
                    new TextBlock { Text = "Does your phone show the same code?", TextWrapping = TextWrapping.Wrap },
                },
            },
            PrimaryButtonText = "Codes match",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        using var reg = ct.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
        var result = await dialog.ShowAsync();
        ct.ThrowIfCancellationRequested();
        return result == ContentDialogResult.Primary;
    }

    void SetStatus(string text, bool busy)
    {
        StatusText.Text = text;
        Busy.IsActive = busy;
    }

    static BitmapImage Qr(string text)
    {
        using var data = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, drawQuietZones: false);
        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        var writer = stream.AsStreamForWrite();
        writer.Write(png);
        writer.Flush();
        stream.Seek(0);
        image.SetSource(stream);
        return image;
    }
}
