using Windows.Storage;

namespace Palwyn.App;

/// <summary>Daily text log in the package's local data. Never log message, notification or clipboard content.</summary>
static class Log
{
    static readonly Lock Gate = new();
    public static string Folder { get; } = Path.Combine(ApplicationData.Current.LocalFolder.Path, "logs");

    public static void Info(string message) => Write("INF", message);
    public static void Error(string message, Exception? e) => Write("ERR", e is null ? message : $"{message}: {e}");

    public static void Prune(int keepDays = 7)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Folder, "palwyn-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"palwyn-{DateTime.Now:yyyyMMdd}.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { } // logging must never take the app down
        catch (UnauthorizedAccessException) { }
    }
}
