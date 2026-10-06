namespace CafePosApp.Services;

// The seams the ViewModels take live in CafePos.Presentation, which references only
// Microsoft.Maui.Graphics — so this file implements interfaces from there, not declares them.
using CafePosApp.Services;
using CafePos.Presentation.Services;

/// <summary>
/// File picking / sharing. Previously the catalogue ViewModel called FilePicker directly
/// (untestable, platform code in the ViewModel) and the photo was copied in the page code-behind.
/// </summary>
public sealed class FileService : IFileService
{
    private const string PhotoDirectoryName = "product_photos";

    public async Task<string?> PickCsvTextAsync(CancellationToken cancellationToken = default)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Выберите CSV файл",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.Android, ["text/csv", "text/comma-separated-values", "application/csv", "*/*"] },
                { DevicePlatform.iOS, ["public.comma-separated-values-text", "public.text"] },
                { DevicePlatform.MacCatalyst, ["public.comma-separated-values-text", "public.text"] },
                { DevicePlatform.WinUI, [".csv", ".txt"] }
            })
        });

        if (result is null) return null;

        await using var stream = await result.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<string?> PickImageAsync(string title, CancellationToken cancellationToken = default)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = title,
            FileTypes = FilePickerFileType.Images
        });

        if (result is null) return null;

        var directory = Path.Combine(FileSystem.AppDataDirectory, PhotoDirectoryName);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{Guid.NewGuid()}{Path.GetExtension(result.FileName)}");

        await using var source = await result.OpenReadAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
        return destination;
    }

    public async Task<string?> PickBackupFileAsync(CancellationToken cancellationToken = default)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Выберите файл резервной копии",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.Android, ["application/octet-stream", "*/*"] },
                { DevicePlatform.iOS, ["public.data"] },
                { DevicePlatform.MacCatalyst, ["public.data"] },
                { DevicePlatform.WinUI, [".db3", ".db", ".bak"] }
            })
        });

        return result?.FullPath;
    }

    public async Task<bool> ShareFileAsync(string filePath, string title, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return false;
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = title,
            File = new ShareFile(filePath)
        });
        return true;
    }

    /// <summary>
    /// The app's log file, or <c>null</c> when it is not there yet.
    /// </summary>
    /// <remarks>
    /// Null rather than a bare filename: <c>AppLog.LogPath</c> falls back to <c>"cafe-pos.log"</c>
    /// with no directory before the writer starts, which would resolve against the process working
    /// directory — somewhere the operator's share sheet could never reach. The path is only useful
    /// once it is a real one, and until then there is nothing to send.
    /// </remarks>
    public string? GetLogFilePath()
    {
        var path = Diagnostics.AppLog.LogPath;
        return File.Exists(path) ? path : null;
    }

    public async Task<string> SaveTextReportAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(FileSystem.AppDataDirectory, "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }
}

