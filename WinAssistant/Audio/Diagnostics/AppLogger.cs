using System.IO;

namespace WinAssistant.Audio.Diagnostics;

public static class AppLogger
{
    private static readonly object SyncRoot = new();

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinAssistant",
        "logs",
        "assistant.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                var directory = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    LogFilePath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never become the reason the app cannot start.
        }
    }
}
