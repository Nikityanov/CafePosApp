using CafePos.Core.Models;
using CafePos.Core.Services;

namespace CafePosApp.Services;

/// <summary>
/// Platform abstractions used by the ViewModels. They keep Shell/FilePicker/DisplayAlert out of
/// the ViewModels, so the ViewModels stay testable and the UI code stays in the platform layer.
/// </summary>
public interface INavigationService
{
    Task GoToOrderDetailsAsync(Guid orderId);
    Task GoBackAsync();
    Task GoToTabAsync(string route);
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
