using CafePos.Core.Data;
using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Tests;

/// <summary>Backup creation, validation and restore against a real SQLite file.</summary>
public class BackupTests
{
    [Fact]
    public async Task Create_and_validate_a_backup()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var backups = host.Get<IBackupService>();

        var backup = await backups.CreateBackupAsync("Тест");

        Assert.True(File.Exists(backup.FilePath));
        Assert.True(backup.SizeBytes > 0);

        var validation = await backups.ValidateAsync(backup.FilePath);
        Assert.True(validation.IsValid, validation.Message);
        // Pinned like the other schema expectations: a backup is only useful if the app that opens it
        // knows exactly what is inside, so this number is the one thing worth failing over.
        Assert.Equal(7, validation.SchemaVersion);

        var list = await backups.GetBackupsAsync();
        Assert.Contains(list, item => item.FileName == backup.FileName);
    }

    [Fact]
    public async Task Validation_rejects_a_garbage_file()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var backups = host.Get<IBackupService>();

        var garbage = Path.Combine(host.Directory, "garbage.db3");
        await File.WriteAllTextAsync(garbage, "definitely not a sqlite database");

        var validation = await backups.ValidateAsync(garbage);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task Restore_brings_back_the_backed_up_state()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        var catalog = host.Get<ICatalogService>();
        var backups = host.Get<IBackupService>();

        await catalog.SaveProductAsync(new Product { Id = Guid.NewGuid(), Name = "До копии", Price = 100m, IsAvailable = true });
        var backup = await backups.CreateBackupAsync("Перед удалением");
        await catalog.SaveProductAsync(new Product { Id = Guid.NewGuid(), Name = "После копии", Price = 200m, IsAvailable = true });

        await backups.RestoreAsync(backup.FilePath);

        var names = (await catalog.GetProductsAsync()).Select(product => product.Name).ToList();
        Assert.Contains("До копии", names);
        Assert.DoesNotContain("После копии", names);
    }
}
