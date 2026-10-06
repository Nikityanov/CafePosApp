using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePos.Presentation.Services;

/// <summary>
/// Platform abstractions used by the ViewModels. They keep Shell/FilePicker/DisplayAlert out of
/// the ViewModels, so the ViewModels stay testable and the UI code stays in the platform layer.
/// </summary>
public interface INavigationService
{
    Task GoToOrderDetailsAsync(Guid orderId);
    Task GoBackAsync();
    Task GoToTabAsync(string route);

    /// <summary>
    /// The opening screen. A PUSH rather than a tab switch, so that leaving it is not a matter of
    /// choosing a different tab: <see cref="AppShell"/> refuses every other destination while no shift
    /// is open, and this route is the one that is allowed.
    /// </summary>
    Task GoToOpenShiftAsync();

    /// <summary>
    /// Takes the operator off the opening screen once a shift exists, and lands on the menu.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GoToTabAsync"/> because the opening screen is a PUSH onto whichever
    /// tab happened to be current, so switching tabs does not remove it — it stays on that tab's
    /// stack as its current page. Switch to the menu and the opening screen is buried, not gone, and
    /// the operator who then taps the shift tab is returned to a page telling them no shift is open
    /// over a shift that has been open for a minute, with no way forward from there.
    /// </remarks>
    Task LeaveOpenShiftAsync();
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string accept = "Да", string cancel = "Отмена");
    Task AlertAsync(string title, string message, string cancel = "ОК");
    Task<string?> PromptAsync(string title, string message, string initialValue = "", string accept = "ОК", string cancel = "Отмена");
    Task<string?> ChooseAsync(string title, string cancel, params string[] options);
}

public interface IFileService
{
    /// <summary>Lets the operator pick a CSV file and returns its content.</summary>
    Task<string?> PickCsvTextAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets the operator pick an image; the file is copied into app storage and its path returned.</summary>
    Task<string?> PickImageAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>Lets the operator pick a database backup file and returns its path.</summary>
    Task<string?> PickBackupFileAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the platform share sheet for the given file.</summary>
    Task<bool> ShareFileAsync(string filePath, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// The path of the app's own log file, or <c>null</c> when there is nothing to share yet.
    /// </summary>
    /// <remarks>
    /// A method on this port because the settings screen offers to send the operator's own diagnostics
    /// out for support, and the log's location is a fact only the platform knows. The alternative was
    /// naming <c>AppLog</c> — a static in the MAUI project — from a ViewModel, which is the one
    /// direction this refactor exists to remove.
    /// <para>
    /// Null rather than a path that may not exist: <c>File.Exists</c> on a guessed path would be the
    /// ViewModel guessing at the file system, and "the log is empty" is a message, not an error.
    /// </para>
    /// </remarks>
    string? GetLogFilePath();

    /// <summary>Writes a text report into app storage and returns its full path.</summary>
    Task<string> SaveTextReportAsync(string fileName, string content, CancellationToken cancellationToken = default);
}

public interface IHapticService
{
    void Click();
    void Warn();
}

/// <summary>Lets the operator pick one of the parked ("held") carts.</summary>
public interface IDraftPicker
{
    Task<Guid?> PickAsync(IReadOnlyList<DraftOrder> drafts, CancellationToken cancellationToken = default);
}

public interface IModifierPicker
{
    Task<string?> PickAsync(ModifierGroup group);
}

public interface IVariantPicker
{
    Task<string?> PickAsync(string productName, List<ProductVariant> variants);
}

/// <summary>
/// What came back from the "when" control: no time at all (as soon as possible), or a clock time.
/// </summary>
/// <remarks>
/// A discriminant and not a bare <see cref="DateTimeOffset"/>: "the order is ASAP" and "the sheet
/// was dismissed" are both an absent time, and collapsing them would mean a tap on the scrim silently
/// moved the order to the ASAP promise. <see cref="Order.RequestedAt"/> being null is the ASAP state,
/// so the two are genuinely different facts and they have to be told apart on the way in.
/// </remarks>
public sealed record TimePickResult(bool IsAsSoonAsPossible, TimeSpan TimeOfDay)
{
    /// <summary>The order is wanted as soon as possible — the promise falls back to the lead time.</summary>
    public static readonly TimePickResult AsSoonAsPossible = new(true, TimeSpan.Zero);

    /// <summary>The customer named a clock time.</summary>
    public static TimePickResult At(TimeSpan timeOfDay) => new(false, timeOfDay);
}

/// <summary>
/// Asks the operator when the order should be ready, and can express "now" as well as a clock time.
/// </summary>
/// <remarks>
/// <b>WHY A NEW SEAM AND NOT A Prompt.</b> <see cref="IDialogService"/> has Prompt, Confirm, Alert and
/// Choose, and none of them can express «14:20» — the operator would type it, and a typed time is
/// parsed by string comparison, which is exactly the kind of free text that produces an order promised
/// for "вчера". The third option has to exist too: an empty time IS «сейчас»
/// (<see cref="Order.RequestedAt"/> is null), so the control is a single choice with two faces, not
/// a checkbox beside a field.
/// <para>
/// The seam is a seam because the SHEET had to change shape, not because the caller needed a different
/// type: on Android a MAUI <c>TimePicker</c> opens the system dial in a second window over the popup,
/// and formatting the value it returned killed the process (<c>TimeSpan.ToString("HH:mm")</c> throws —
/// see <see cref="ClockTime"/> and <c>Views/TimePickerPopup</c>). Neither fact is visible from here,
/// and both were only findable on the device; what stays visible is that the caller names a time of day
/// and gets one back, and that a null result is a dismissal rather than an answer.
/// </para>
/// </remarks>
public interface IOrderTimePicker
{
    /// <param name="title">Sheet title, e.g. «Когда приготовить».</param>
    /// <param name="initial">
    /// The clock time already chosen, or <c>null</c> when the order is as soon as possible — which is
    /// also what the sheet opens on, so the operator sees the current state rather than a blank form.
    /// </param>
    /// <returns>The choice, or <c>null</c> when the sheet was dismissed and nothing changed.</returns>
    Task<TimePickResult?> PickAsync(string title, TimeSpan? initial, CancellationToken cancellationToken = default);
}

/// <summary>
/// A dish a bundle slot may be filled with, as the composition sheet offers it.
/// </summary>
/// <param name="ProductId">The dish.</param>
/// <param name="Label">
/// What the sheet prints. Pre-composed by the caller rather than assembled here, because the only case
/// that needs composing is a substitution — the slot's dish ran out and this is the replacement — and
/// the words for that belong next to the rule that decides it.
/// </param>
/// <param name="UnitKopecks">
/// What one of this dish is charged inside the bundle, in kopecks, already resolved by
/// <see cref="CafePos.Core.Common.ComboPricing.ResolveUnitKopecks"/>: a slot priced explicitly keeps that
/// price, otherwise the dish's own price is used. The sheet shows a running total from these, and the
/// price that is actually charged is recomputed from the catalogue at checkout.
/// </param>
/// <param name="ReferenceKopecks">
/// What this dish costs on its own, in kopecks — Simphony's "Prep Cost", and the figure
/// <see cref="CafePos.Core.Models.OrderItemComponent.ReferencePriceKopecks"/> keeps beside
/// <c>UnitPriceKopecks</c> so that one sale can answer two reports. The sheet shows what the bundle
/// saves against it.
/// </param>
public sealed record ComboSlotOption(Guid ProductId, string Label, long UnitKopecks, long ReferenceKopecks);

/// <summary>One slot of a bundle as the cashier left it: which dish, and how many of it per unit.</summary>
public sealed record ComboSlotChoice(Guid ProductId, int QuantityPerUnit);

/// <summary>
/// What the composition sheet opens on: the dishes it may offer, what is in the sheet already, and
/// the bundle's own price.
/// </summary>
/// <remarks>
/// ONE REQUEST SHAPE FOR BOTH CASES, and that is the whole point: a catalogue bundle arrives with a
/// filled <see cref="Selection"/> and <see cref="Empty"/> arrives with none. They are the
/// same sheet, the same rows and the same confirm — a custom build is not a second mechanic, it is
/// this one started empty.
/// </remarks>
/// <param name="Title">Sheet title, naming the bundle.</param>
/// <param name="Options">The dishes a slot may hold. See <see cref="ComboSlotOption"/>.</param>
/// <param name="Selection">The slots already in the bundle; empty for a custom build.</param>
/// <param name="PriceKopecks">
/// The bundle's own price, in kopecks — what the till charges. Shown as the sheet's main figure,
/// with the à la carte sum of the slots as the reference it is measured against. Zero when the
/// caller does not know it (a custom build from scratch), in which case the sheet shows the sum
/// alone.
/// </param>
public sealed record ComboEditorRequest(
    string Title,
    IReadOnlyList<ComboSlotOption> Options,
    IReadOnlyList<ComboSlotChoice> Selection,
    long PriceKopecks = 0)
{
    /// <summary>An empty build: the sheet with nothing chosen yet.</summary>
    public static ComboEditorRequest Empty { get; } = new("Состав комбо", [], []);
}

/// <summary>
/// Opens the one composition sheet that serves both a catalogue bundle and a custom build.
/// </summary>
public interface IComboEditor
{
    /// <returns>
    /// The slots the cashier settled on, or <c>null</c> when the sheet was dismissed — a dismissal is
    /// not an empty composition, and treating it as one would drop a bundle off the cart on a stray tap.
    /// </returns>
    Task<IReadOnlyList<ComboSlotChoice>?> ComposeAsync(
        ComboEditorRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shows the overflow menu of a list row and reports the chosen action.
/// </summary>
/// <remarks>
/// .NET MAUI has no cross-platform context menu: there is no <c>ContextMenu</c> control at all, and
/// the UWP-era <c>MenuFlyout</c> is reachable only through the explicit
/// <c>IContextFlyoutElement.ContextFlyout</c> interface, so it cannot be declared in XAML. The
/// catalogue therefore uses an action sheet, which the app already runs for the three pickers.
/// </remarks>
public interface ICatalogActionSheet
{
    /// <summary>Returns the key of the chosen action, or null if the sheet was dismissed.</summary>
    Task<string?> ChooseAsync(string title, IReadOnlyList<CatalogAction> actions, CancellationToken cancellationToken = default);
}
