// palwyn-linux: the Linux PC side, headless for now (docs/superpowers/plans/2026-10-05-linux-port.md, Phase 2).
// Pairs with the phone, keeps it connected and shows its notifications on the desktop.
using System.Text;
using Palwyn.Core;
using Palwyn.Core.Link;
using Palwyn.Linux;
using QRCoder;

const int PhonePort = 47800;
var dataDir = Path.Combine(
    Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
        ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
    "palwyn");
var identity = IdentityFile.Load(dataDir);
var pcFingerprint = Fingerprint.Of(identity);
var phones = new PairedPhones(Path.Combine(dataDir, "paired-phones.json"));
var pcName = Environment.MachineName;

using var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { quit.Cancel(); } catch (ObjectDisposedException) { } };

try
{
    return args switch
    {
        ["pair"] => await Pair(null),
        ["pair", "--address", var address] => await Pair(address),
        ["run"] => await Run(),
        ["status"] => Status(),
        ["unpair"] => Unpair(),
        _ => Usage(),
    };
}
catch (OperationCanceledException) when (quit.IsCancellationRequested)
{
    return 130;
}

int Usage()
{
    Console.WriteLine("""
        Usage: palwyn-linux <command>
          pair                       show a QR code to scan with Palwyn on the phone, then pair
          pair --address <ip[:port]> the same, for a network where the PC can't find the phone by itself
          run                        stay connected and show the phone's notifications on this desktop
          status                     this PC's id and the paired phone
          unpair                     forget the paired phone
        """);
    return 2;
}

async Task<int> Pair(string? address)
{
    (string Host, int Port)? manual = null;
    if (address is not null && (manual = PairedPhone.ParseAddress(address, PhonePort)) is null)
    {
        Console.Error.WriteLine($"Not an address: {address}");
        return 2;
    }
    var invite = PairingInvite.Create(pcFingerprint, pcName);
    var expires = DateTimeOffset.UtcNow + PcPairing.InviteLifetime;
    Console.WriteLine(TerminalQr(invite.ToUri()));
    Console.WriteLine("Scan this with your phone's camera and open the Palwyn link. It works once, for 5 minutes.");

    using var ct = CancellationTokenSource.CreateLinkedTokenSource(quit.Token);
    ct.CancelAfter(PcPairing.InviteLifetime);
    DiscoveredPhone phone;
    if (manual is { } m)
    {
        Console.WriteLine("Then press Enter.");
        await Task.Run(Console.ReadLine, ct.Token).WaitAsync(ct.Token);
        phone = new DiscoveredPhone("", "", null, null, m.Host, m.Port);
    }
    else
    {
        var tag = "qr:" + Hex.Of(pcFingerprint)[..8];
        var found = new TaskCompletionSource<DiscoveredPhone>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var discovery = new AvahiDiscovery();
        discovery.Found += d => { if (d.PairMode == tag) found.TrySetResult(d); };
        discovery.Start();
        if (discovery.Problem is { } problem)
        {
            Console.Error.WriteLine($"{problem} Use: palwyn-linux pair --address <the phone's IP address>");
            return 1;
        }
        try { phone = await found.Task.WaitAsync(ct.Token); }
        catch (OperationCanceledException) when (!quit.IsCancellationRequested)
        {
            Console.Error.WriteLine("No phone came back within 5 minutes. If the phone did scan, this network may hide it: try pair --address.");
            return 1;
        }
    }

    Console.WriteLine($"Pairing with {phone.Name ?? "the phone"}…");
    try
    {
        // Before pairing the phone's certificate is unknown; the pairing proof is what authenticates it.
        await using var link = await LinkConnection.ConnectAsync(phone.Host, phone.Port, identity, _ => true, ct.Token);
        var fp = await PcPairing.CompleteQrAsync(link, invite, expires, pcName, ct.Token);
        var paired = new PairedPhone(Fingerprint.DeviceId(fp), phone.Name ?? "Android phone", Hex.Of(fp),
            phone.Host, phone.Port, DateTimeOffset.UtcNow, address);
        foreach (var old in phones.All()) phones.Remove(old.DeviceId); // one phone for now
        phones.Save(paired);
        Console.WriteLine($"Paired with {paired.Name} ({Fingerprint.Display(paired.DeviceId)}). Now run: palwyn-linux run");
        return 0;
    }
    catch (Exception e) when (e is PairingException or IOException or System.Net.Sockets.SocketException
                                  or System.Security.Authentication.AuthenticationException or Palwyn.Core.Protocol.ProtocolException)
    {
        Console.Error.WriteLine($"Pairing failed: {e.Message}");
        return 1;
    }
}

async Task<int> Run()
{
    if (phones.All().FirstOrDefault() is not { } phone)
    {
        Console.Error.WriteLine("No phone is paired. Run: palwyn-linux pair");
        return 1;
    }
    // The shared hub (as on Windows): connects, follows the phone's address through discovery, reconnects.
    var host = new LinuxHost(new DesktopNotifier());
    var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    host.Blocked += why => blocked.TrySetResult(why);
    var discovery = new AvahiDiscovery();
    using var link = new LinkManager(host, identity, dataDir, discovery, new NoUsb());
    Console.WriteLine($"Connecting to {phone.Name}. Ctrl+C to stop.");
    link.Start();
    if (discovery.Problem is { } problem) Console.Error.WriteLine($"{problem} Only the last known address is tried.");
    var ended = await Task.WhenAny(blocked.Task, Task.Delay(Timeout.Infinite, quit.Token).ContinueWith(_ => { }));
    if (ended != blocked.Task) return 0;
    Console.Error.WriteLine(blocked.Task.Result);
    return 1;
}

int Status()
{
    Console.WriteLine($"This PC: {pcName}, id {Fingerprint.Display(Fingerprint.DeviceId(pcFingerprint))}");
    var phone = phones.All().FirstOrDefault();
    Console.WriteLine(phone is null ? "No phone paired."
        : $"Phone: {phone.Name}, id {Fingerprint.Display(phone.DeviceId)}, {(phone.Address is null ? "last seen at" : "address")} {phone.Endpoint.Host}:{phone.Endpoint.Port}");
    return 0;
}

int Unpair()
{
    var all = phones.All();
    foreach (var p in all) phones.Remove(p.DeviceId);
    Console.WriteLine(all.Count == 0 ? "No phone was paired."
        : "Forgotten. Remove this PC in Palwyn on the phone too, or the phone keeps it in its list.");
    return 0;
}

/// <summary>The QR code in half-block characters, black on white whatever the terminal's colours, so it scans.</summary>
static string TerminalQr(string text)
{
    using var data = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
    var m = data.ModuleMatrix; // includes the quiet zone
    var sb = new StringBuilder();
    for (int y = 0; y < m.Count; y += 2)
    {
        sb.Append("\e[30;47m");
        for (int x = 0; x < m.Count; x++)
            sb.Append((m[y][x], y + 1 < m.Count && m[y + 1][x]) switch
            {
                (true, true) => '█', (true, false) => '▀', (false, true) => '▄', _ => ' ',
            });
        sb.Append("\e[0m\n");
    }
    return sb.ToString();
}
