using System.Text;

namespace CafePosApp.Diagnostics;

/// <summary>
/// Small, thread safe, rotating log file writer.
/// The previous logger opened and appended the file on the UI thread for every single message
/// (including a dozen calls during startup) and never rotated the file.
/// </summary>
public sealed class LogFileWriter
{
    private const long MaxFileSizeBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 3;

    private readonly object syncRoot = new();
    private readonly string directory;
    private readonly string fileNamePrefix;
    private string currentFilePath;
    private long currentSize;

    public LogFileWriter(string directory, string fileNamePrefix = "cafe-pos")
    {
        this.directory = directory;
        this.fileNamePrefix = fileNamePrefix;
        Directory.CreateDirectory(directory);
        currentFilePath = Path.Combine(directory, $"{fileNamePrefix}.log");
        currentSize = File.Exists(currentFilePath) ? new FileInfo(currentFilePath).Length : 0;
    }

    public string CurrentLogPath => currentFilePath;

    public void Write(DateTimeOffset timestamp, string level, string message)
    {
        var line = $"{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";

        try
        {
            lock (syncRoot)
            {
                if (currentSize > MaxFileSizeBytes) Roll();
                File.AppendAllText(currentFilePath, line, Encoding.UTF8);
                currentSize += Encoding.UTF8.GetByteCount(line);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[LogFileWriter] Cannot write to {currentFilePath}: {exception.Message}");
        }
    }

    private void Roll()
    {
        for (var index = MaxFiles - 1; index >= 1; index--)
        {
            var source = Path.Combine(directory, $"{fileNamePrefix}.{index}.log");
            var target = Path.Combine(directory, $"{fileNamePrefix}.{index + 1}.log");
            if (!File.Exists(source)) continue;
            if (File.Exists(target)) File.Delete(target);
            File.Move(source, target);
        }

        if (File.Exists(currentFilePath)) File.Move(currentFilePath, Path.Combine(directory, $"{fileNamePrefix}.1.log"));
        currentSize = 0;
    }
}
