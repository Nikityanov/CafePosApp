using CafePos.Core.Data;
using CafePos.Core.DependencyInjection;
using CafePos.Core.Services;
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

    /// <summary>
    /// Opens a shift with no change in the drawer and returns its id.
    /// </summary>
    /// <remarks>
    /// A test about orders, payments or reports needs a shift to hang them on, and needs it to be
    /// EMPTY of change so that "what is in the drawer" keeps meaning what it meant before the float
    /// existed. This is the replacement for the old
    /// <c>GetOrCreateActiveShiftAsync</c>, which opened one silently on demand — so a test that
    /// forgot to open a shift used to get one anyway, and a test that expected an empty drawer got
    /// whatever the service decided. Now the shift is something the test asks for, which is also what
    /// the production code has to do.
    /// </remarks>
    public static async Task<Guid> OpenEmptyShiftAsync(IOrderService orders) =>
        (await orders.OpenShiftAsync(0)).Id;
}
