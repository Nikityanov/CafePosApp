namespace CafePosApp.Services;

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

    public async Task<string> SaveTextReportAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(FileSystem.AppDataDirectory, "reports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return path;
    }
}
