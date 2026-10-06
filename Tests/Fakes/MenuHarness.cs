using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Schema;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CafePos.Presentation.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CafePosApp.Tests;

/// <summary>
/// Assembles a real <see cref="MenuViewModel"/>: real Core services over a real database, platform
/// seams replaced by fakes.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is mocked. The catalog, the combos and the draft store are the production
/// implementations over a throwaway SQLite file, so a test here exercises the same query and the same
/// domain rules the app does. Only the sheets are replaced, because those are the parts that need a
/// window.
/// </para>
/// <para>
/// The fakes are exposed as properties rather than kept private, so a test can assert on what the
/// operator was shown — that a picker opened, that a sheet was NOT opened, that a dialog was not
/// raised — which is most of what the cart does that is worth protecting.
/// </para>
/// </remarks>
internal sealed class MenuHarness : IDisposable
{
    private readonly TestHost.Host host;

    private MenuHarness(TestHost.Host host, DateTimeOffset now, TimePickResult? timePickAnswer = null)
    {
        this.host = host;
        Now = now;

        // Before anything else, because it is global state that every ViewModel colour read goes
        // through, and MenuViewModel reads one in its constructor. Registering here rather than in each
        // test means the locator has exactly one place it is satisfied.
        Palette = new FakePalette();
        PaletteAccess.Set(Palette);

        VariantPicker = new FakeVariantPicker();
        ModifierPicker = new FakeModifierPicker();
        DraftPicker = new FakeDraftPicker();
        Dialogs = new FakeDialog();
        TimePicker = new FakeTimePicker(timePickAnswer);
        ComboEditor = new FakeComboEditor();

        ViewModel = new MenuViewModel(
            host.Get<ICatalogService>(),
            host.Get<IComboService>(),
            host.Get<ICheckoutService>(),
            host.Get<IDraftOrderService>(),
            host.Get<IInventoryService>(),
            ModifierPicker,
            VariantPicker,
            DraftPicker,
            Dialogs,
            new FakeHaptics(),
            new FakePaymentSheet(),
            TimePicker,
            ComboEditor,
            new FakeNavigation(),
            host.Get<IShiftSession>(),
            FixedTime(now),
            NullLogger<MenuViewModel>.Instance);
    }

    /// <summary>A fixed clock, so a promised time and an overdue badge are the same on every run.</summary>
    private static TimeProvider FixedTime(DateTimeOffset now) => new FixedTimeProvider(now);

    public DateTimeOffset Now { get; }

    public FakePalette Palette { get; }

    public MenuViewModel ViewModel { get; }

    public FakeVariantPicker VariantPicker { get; }

    public FakeModifierPicker ModifierPicker { get; }

    public FakeDraftPicker DraftPicker { get; }

    public FakeDialog Dialogs { get; }

    public FakeTimePicker TimePicker { get; }

    public FakeComboEditor ComboEditor { get; }

    public ICatalogService Catalog => host.Get<ICatalogService>();

    public IComboService Combos => host.Get<IComboService>();

    public ICheckoutService Checkout => host.Get<ICheckoutService>();

    public IDraftOrderService Drafts => host.Get<IDraftOrderService>();

    public IInventoryService Inventory => host.Get<IInventoryService>();

    public TestHost.Host Host => host;

    /// <summary>
    /// Builds the harness on a MIGRATED database.
    /// </summary>
    /// <remarks>
    /// <see cref="TestHost"/> wires the services but does not create the schema — production migrates
    /// on startup, and <c>AddCafePosCore</c> only registers <c>SchemaMigrator</c>. Skipping it here does
    /// not fail loudly: the first query says <c>no such table: Categories</c>, which points at the
    /// catalog rather than at the harness that forgot to provision it. Migrating in the factory means
    /// the only way to get a working catalog is to get a working harness.
    /// </remarks>
    public static async Task<MenuHarness> CreateAsync(
        DateTimeOffset? now = null,
        bool seedDemoData = false,
        TimePickResult? timePickAnswer = null)
    {
        var host = TestHost.Create(seedDemoData);
        try
        {
            await host.Get<SchemaMigrator>().MigrateAsync();
        }
        catch
        {
            // Dispose removes the temp directory, so a failed migration does not leave a stray file per
            // test run.
            host.Dispose();
            throw;
        }

        return new MenuHarness(host, now ?? DefaultNow, timePickAnswer);
    }

    /// <summary>
    /// A fixed evening, so a promised time and an overdue badge read the same on every run.
    /// </summary>
    private static readonly DateTimeOffset DefaultNow = DateTimeOffset.Parse("2026-10-05T17:00:00+03:00");

    public async Task<Product> AddProductAsync(string name, decimal price, Guid? categoryId = null, bool hasVariants = false)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Price = price,
            CategoryId = categoryId,
            IsAvailable = true,
            HasVariants = hasVariants,
        };

        if (hasVariants)
        {
            product.Variants =
            [
                new ProductVariant { Id = Guid.NewGuid(), ProductId = product.Id, Name = "0,3 л", PriceKopecks = 0 },
                new ProductVariant { Id = Guid.NewGuid(), ProductId = product.Id, Name = "0,5 л", PriceKopecks = 3000 },
            ];
        }

        await Catalog.SaveProductAsync(product);
        return product;
    }

    public async Task<Category> AddCategoryAsync(string name)
    {
        var category = new Category { Id = Guid.NewGuid(), Name = name };
        await Catalog.SaveCategoryAsync(category);
        return category;
    }

    public void Dispose() => host.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}