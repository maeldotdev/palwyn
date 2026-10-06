using System.Diagnostics;
using Palwyn.Core;

namespace Palwyn.Core.Emergency;

/// <summary>Runs adb for the emergency screen and Rescue files. Each PC app sets <see cref="Executable"/> (its bundled
/// adb, else the SDK's or PATH's) and <see cref="Log"/> at start.</summary>
public static class Adb
{
    public static string? Executable { get; set; }
    public static Action<string> Log { get; set; } = _ => { };
    public static bool Available => Executable is not null;

    /// <summary>Exit code and standard output. Arguments are passed as a list, so paths need no quoting here
    /// (a remote shell command still does: AdbOutput.ShellQuote).</summary>
    public static async Task<(int Exit, string Output)> RunAsync(CancellationToken ct, params string[] args)
    {
        using var p = Start(args);
        var output = p.StandardOutput.ReadToEndAsync(ct);
        _ = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(); } catch (InvalidOperationException) { } throw; }
        return (p.ExitCode, await output);
    }

    /// <summary>A started adb process with redirected output (the caller reads or drains it).</summary>
    public static Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Executable ?? throw new EmergencyException(EmergencyException.Failed))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // adb writes UTF-8; a windowed app would otherwise decode it with the ANSI code page ("ñ" → "Ã±")
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        return Process.Start(info)!;
    }

    /// <summary>Phones on a USB cable, allowed or not (an "unauthorized" one is still asking, or never allowed this PC).</summary>
    public static async Task<IReadOnlyList<AdbDevice>> UsbDevicesAsync(CancellationToken ct = default)
    {
        if (!Available) return [];
        var (exit, output) = await RunAsync(ct, "devices", "-l");
        return exit == 0 ? AdbOutput.ParseDevices(output).Where(d => d.IsUsb).ToList() : [];
    }
}
