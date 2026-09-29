// Phase 0 spike: can Palwyn see and drive the Windows Bluetooth Hands-Free (HFP)
// phone-line transport that carries cellular call audio? Run unpackaged (dotnet run)
// and packaged (register-packaged.ps1) to compare.
using Windows.ApplicationModel.Calls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

var logPath = Path.Combine(AppContext.BaseDirectory, "spike-output.txt");
File.WriteAllText(logPath, "");
void Log(string s) { Console.WriteLine(s); File.AppendAllText(logPath, s + Environment.NewLine); }
async Task Step(string name, Func<Task> body)
{
    try { await body(); }
    catch (Exception e) { Log($"  {name}: {e.GetType().Name} 0x{e.HResult:X8} {e.Message.Trim()}"); }
}

bool packaged = true;
try { _ = Windows.ApplicationModel.Package.Current.Id; } catch { packaged = false; }
Log($"OS: {Environment.OSVersion}  packaged={packaged}");

foreach (var r in await Radio.GetRadiosAsync())
    Log($"Radio: {r.Kind} state={r.State}");

var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
Log($"Paired Bluetooth devices: {paired.Count}");
foreach (var d in paired) Log($"  - {d.Name}");

var transports = await DeviceInformation.FindAllAsync(
    PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth));
Log($"HFP phone-line transports: {transports.Count}");

foreach (var d in transports)
{
    var t = PhoneLineTransportDevice.FromId(d.Id);
    Log($"- {d.Name}: audio={t.AudioRoutingStatus} inBandRing={t.InBandRingingEnabled}");
    await Step("RequestAccessAsync", async () => Log($"  access={await t.RequestAccessAsync()}"));
    await Step("RegisterApp", () => { t.RegisterApp(); Log($"  registered={t.IsRegistered()}"); return Task.CompletedTask; });
    await Step("ConnectAsync", async () => Log($"  connect={await t.ConnectAsync()}"));
}

await Step("PhoneLines", async () =>
{
    var store = await PhoneCallManager.RequestStoreAsync();
    var watcher = store.RequestLineWatcher();
    var done = new TaskCompletionSource();
    watcher.LineAdded += async (_, e) =>
    {
        var line = await PhoneLine.FromIdAsync(e.LineId);
        Log($"  line '{line.DisplayName}' transport={line.Transport} canDial={line.CanDial} network={line.NetworkName}");
    };
    watcher.EnumerationCompleted += (_, _) => done.TrySetResult();
    watcher.Start();
    await Task.WhenAny(done.Task, Task.Delay(5000));
    Log($"  line enumeration status={watcher.Status}");
});

Log("done");
