namespace CafePosApp.Diagnostics;

/// <summary>
/// Bootstrap time logging: used before the DI container (and therefore ILogger) exists,
/// and for exceptions that happen outside of any request (AppDomain / unobserved tasks).
/// </summary>
public static class AppLog
{
    private static LogFileWriter? writer;

    public static string LogPath => writer?.CurrentLogPath ?? "cafe-pos.log";

    public static void Start(string directory)
    {
        writer ??= new LogFileWriter(directory);
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string stage, Exception? exception)
    {
        if (exception is null)
        {
            Write("ERROR", $"{stage}: unknown error");
            return;
        }

        Write("ERROR", $"{stage}: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");
    }

    private static void Write(string level, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[{level}] {message}");
        writer?.Write(DateTimeOffset.Now, level, message);
    }
}
