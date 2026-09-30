using CafePos.Core.Data;
using CafePos.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp.Tests;

/// <summary>
/// Builds a real Core service provider against a throwaway SQLite database in %TEMP%.
/// Integration tests exercise the same wiring the MAUI app uses (AddCafePosCore).
/// </summary>
public static class TestHost
{
    public sealed class Host : IDisposable
    {
        public Host(ServiceProvider services, string directory)
        {
            Services = services;
            Directory = directory;
        }

        public ServiceProvider Services { get; }
        public string Directory { get; }
        public string DatabasePath => Path.Combine(Directory, "test.db3");
        public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

        public void Dispose()
        {
            Services.Dispose();
            // SQLite may keep the file handle for a moment after the last context is disposed.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (System.IO.Directory.Exists(Directory))
                        System.IO.Directory.Delete(Directory, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    public static Host Create(bool seedDemoData = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cafepos-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCafePosCore(new DatabaseOptions
        {
            DatabasePath = Path.Combine(directory, "test.db3"),
            BackupDirectory = Path.Combine(directory, "backups"),
            BackupRetention = 5,
            // Keep the startup backup out of timing-sensitive tests.
            AutomaticBackupInterval = TimeSpan.FromDays(365),
            SeedDemoData = seedDemoData
        });

        return new Host(services.BuildServiceProvider(), directory);
    }
}
