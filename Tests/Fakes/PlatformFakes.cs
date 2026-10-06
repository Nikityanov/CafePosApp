using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation;
using CafePos.Presentation.Services;
using CafePos.Presentation.ViewModels;
using Microsoft.Maui.Graphics;

namespace CafePosApp.Tests;

/// <summary>
/// Stand-ins for the platform seams <c>MenuViewModel</c> takes.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is a MAUI-facing port: a sheet, a picker, a dialog, the device's back button.
/// The MAUI implementations open Popups and read <c>Shell.Current</c>, so a test cannot use them and a
/// test must not depend on them either — a test that can only pass on an emulator is not a test.
/// </para>
/// <para>
/// <b>THEY ARE FAKES, NOT STUBS, AND THE DIFFERENCE IS THE POINT.</b> A stub returns a default and
/// records nothing. Each of these returns a value the test chose and RECORDS what was asked, so a test
/// can assert that the operator was not shown a picker they did not ask for. The recording is the
/// reason these are worth the lines: "the cart did not call the variant picker" is a real regression
/// (a dish with variants that silently sold the wrong size) and it is invisible without it.
/// </para>
/// <para>
/// Every fake is handed its answer in the constructor and has no branching of its own, so a test reads
/// as the scenario it is rather than as configuration.
/// </para>
/// </remarks>
internal sealed class FakeNavigation : INavigationService
{
    public Task GoToOrderDetailsAsync(Guid orderId) => Task.CompletedTask;

    public Task GoBackAsync() => Task.CompletedTask;

    public Task GoToTabAsync(string route) => Task.CompletedTask;

    public Task GoToOpenShiftAsync() => Task.CompletedTask;

    public Task LeaveOpenShiftAsync() => Task.CompletedTask;
}

internal sealed class FakeDialog : IDialogService
{
    private readonly bool confirm;

    public FakeDialog(bool confirm = true) => this.confirm = confirm;

    /// <summary>Every prompt this was asked, so a test can assert the operator was never interrupted.</summary>
    public List<string> Prompted { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Да", string cancel = "Отмена") =>
        Task.FromResult(confirm);

    public Task AlertAsync(string title, string message, string cancel = "ОК") => Task.CompletedTask;

    public Task<string?> PromptAsync(string title, string message, string initialValue = "", string accept = "ОК", string cancel = "Отмена")
    {
        Prompted.Add(title);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> ChooseAsync(string title, string cancel, params string[] options) =>
        Task.FromResult<string?>(null);
}

internal sealed class FakeHaptics : IHapticService
{
    public int Clicks { get; private set; }

    public int Warnings { get; private set; }

    public void Click() => Clicks++;

    public void Warn() => Warnings++;
}

internal sealed class FakeFile : IFileService
{
    public Task<string?> PickCsvTextAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> PickImageAsync(string title, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<string?> PickBackupFileAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<bool> ShareFileAsync(string filePath, string title, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public string? GetLogFilePath() => null;

    public Task<string> SaveTextReportAsync(string fileName, string content, CancellationToken cancellationToken = default) =>
        Task.FromResult(Path.Combine(Path.GetTempPath(), fileName));
}

/// <summary>A picker that returns the answer it was given, and counts how often it was opened.</summary>
internal sealed class FakeModifierPicker : IModifierPicker
{
    private readonly string? answer;

    public FakeModifierPicker(string? answer = null) => this.answer = answer;

    public int Opened { get; private set; }

    public Task<string?> PickAsync(ModifierGroup group)
    {
        Opened++;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeVariantPicker : IVariantPicker
{
    private readonly string? answer;

    public FakeVariantPicker(string? answer = null) => this.answer = answer;

    public int Opened { get; private set; }

    public Task<string?> PickAsync(string productName, List<ProductVariant> variants)
    {
        Opened++;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeDraftPicker : IDraftPicker
{
    private readonly Guid? answer;

    public FakeDraftPicker(Guid? answer = null) => this.answer = answer;

    public Task<Guid?> PickAsync(IReadOnlyList<DraftOrder> drafts, CancellationToken cancellationToken = default) =>
        Task.FromResult(answer);
}

/// <summary>
/// A time picker whose answer is fixed, including the NULL case — a dismissal, which is not the same
/// fact as "as soon as possible" and which the fake can therefore express separately.
/// </summary>
internal sealed class FakeTimePicker : IOrderTimePicker
{
    private readonly TimePickResult? answer;

    public FakeTimePicker(TimePickResult? answer = null) => this.answer = answer;

    public int Opened { get; private set; }

    public Task<TimePickResult?> PickAsync(string title, TimeSpan? initial, CancellationToken cancellationToken = default)
    {
        Opened++;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeComboEditor : IComboEditor
{
    private readonly IReadOnlyList<ComboSlotChoice>? answer;

    public FakeComboEditor(IReadOnlyList<ComboSlotChoice>? answer = null) => this.answer = answer;

    public int Opened { get; private set; }

    /// <summary>The request the cart built, so a test can assert what the operator was offered.</summary>
    public ComboEditorRequest? LastRequest { get; private set; }

    public Task<IReadOnlyList<ComboSlotChoice>?> ComposeAsync(ComboEditorRequest request, CancellationToken cancellationToken = default)
    {
        Opened++;
        LastRequest = request;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeCatalogActionSheet : ICatalogActionSheet
{
    private readonly string? answer;

    public FakeCatalogActionSheet(string? answer = null) => this.answer = answer;

    public Task<string?> ChooseAsync(string title, IReadOnlyList<CatalogAction> actions, CancellationToken cancellationToken = default) =>
        Task.FromResult(answer);
}

internal sealed class FakePaymentSheet : IPaymentSheet
{
    private readonly PaymentSheetResult? answer;

    public FakePaymentSheet(PaymentSheetResult? answer = null) => this.answer = answer;

    public int Opened { get; private set; }

    public Task<PaymentSheetResult?> CollectAsync(PaymentSheetRequest request, CancellationToken cancellationToken = default)
    {
        Opened++;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeStockSheet : IStockDispositionSheet
{
    private readonly StockDisposition? answer;

    public FakeStockSheet(StockDisposition? answer = null) => this.answer = answer;

    /// <summary>
    /// Returns the enum directly because the interface does — the sheet is a single choice between two
    /// dispositions, and wrapping it in a result record would only have invited someone to add an
    /// "IsNone" case that the domain already refuses.
    /// </summary>
    public Task<StockDisposition?> ChooseAsync(StockDispositionSheetRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(answer);
}

/// <summary>
/// A palette that answers every key with a distinct colour derived from the key itself.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IPalette"/> exists because <c>Application.Current.Resources</c> cannot cross the project
/// boundary, so it is reached through <see cref="PaletteAccess"/> - a service locator, and therefore
/// global state a test must set up before it can build any ViewModel at all. <c>MenuHarness</c>
/// registers one of these so that cost is paid once, in one place, where it is visible.
/// </para>
/// <para>
/// <b>THE COLOURS ARE DERIVED, NOT PICKED.</b> Nothing here cares what a category chip looks like, so
/// returning one fixed colour would hide the only thing a test could learn from this: that two
/// different keys produced two different colours. Hashing the key means a test which asserted
/// "chips differ" would still pass if the palette ever stopped doing so - which is exactly the drift
/// <c>IPalette</c> was written to prevent.
/// </para>
/// </remarks>
internal sealed class FakePalette : IPalette
{
    /// <summary>Every key this was asked for, in order - so a test can assert what was looked up.</summary>
    public List<string> KeysAsked { get; } = [];

    public Color Resolve(string lightKey, string darkKey)
    {
        KeysAsked.Add(lightKey);
        return ColourFor(lightKey);
    }

    private static Color ColourFor(string key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in key)
            {
                hash ^= c;
                hash *= 16777619;
            }

            // All four as int, not byte: Color.FromRgba has byte AND int overloads, and a mix of casts
            // lands on neither unambiguously.
            return Color.FromRgba((int)(hash >> 24), (int)(hash >> 16), (int)(hash >> 8), 255);
        }
    }
}
