using CafePos.Core.Models;

namespace CafePos.Core.Services;

// The data behind the catalogue row overflow menu. Deliberately the only part of that feature
// left in the core: the interface that presents it (ICatalogActionSheet) is a MAUI platform gap
// and now lives with the other platform abstractions in Services/Abstractions/PlatformServices.cs.

/// <summary>One entry of a row's overflow action sheet.</summary>
/// <param name="Key">Stable identifier the caller switches on; never shown to the operator.</param>
/// <param name="Title">The button label.</param>
/// <param name="IsDestructive">Renders the entry in the danger colour.</param>
public sealed record CatalogAction(string Key, string Title, bool IsDestructive = false);

/// <summary>Keys of <see cref="CatalogAction.Key"/>, used to switch on the chosen action.</summary>
public static class CatalogActionKeys
{
    public const string Edit = "edit";
    public const string Copy = "copy";
    public const string Delete = "delete";
    public const string Restore = "restore";
    public const string ToggleAvailability = "toggle-availability";
    public const string RemoveFromGroup = "remove-from-group";
}

/// <summary>
/// Builds the overflow menu contents for each catalogue row type.
/// </summary>
/// <remarks>
/// This is a deliberate testability trade, and the reason it stays in the core rather than moving
/// down to the app with <c>ICatalogActionSheet</c>: keeping the available set of actions, the
/// wording and the destructive flag as plain data is what lets <c>Tests/CatalogActionsTests.cs</c>
/// assert on them from a plain <c>net10.0</c> test project that references only <c>CafePos.Core</c>.
/// <para>
/// The accepted cost is that the Russian operator-facing wording ("Редактировать", "Удалить",
/// "Снять с продажи") lives in the platform-independent domain assembly. Moving that wording to the
/// app layer would be the purer layering, but it would drag the action set with it and mean
/// rewriting <c>CatalogActionsTests</c> — and the tests would then need a project reference to the
/// MAUI app project, which is multi-targeted and not referencable from that test host. The trade is
/// worth it; it is recorded here so the next reader does not "fix" it.
/// </para>
/// </remarks>
public static class CatalogActions
{
    public static IReadOnlyList<CatalogAction> ForProduct(Product product) =>
    [
        new(CatalogActionKeys.Edit, "Редактировать"),
        new(CatalogActionKeys.Copy, "Создать копию"),
        Availability(product.IsAvailable),
        // A soft-deleted row can only be brought back, so the pair is mutually exclusive.
        product.IsDeleted
            ? new CatalogAction(CatalogActionKeys.Restore, "Вернуть в каталог")
            : new CatalogAction(CatalogActionKeys.Delete, "Удалить", IsDestructive: true)
    ];

    public static IReadOnlyList<CatalogAction> ForCategory() =>
    [
        new(CatalogActionKeys.Edit, "Переименовать"),
        new(CatalogActionKeys.Delete, "Удалить раздел", IsDestructive: true)
    ];

    public static IReadOnlyList<CatalogAction> ForModifierGroup() =>
    [
        new(CatalogActionKeys.Edit, "Переименовать группу"),
        new(CatalogActionKeys.Delete, "Удалить группу", IsDestructive: true)
    ];

    public static IReadOnlyList<CatalogAction> ForModifierOption(ModifierOption option) =>
    [
        Availability(option.IsAvailable),
        new(CatalogActionKeys.RemoveFromGroup, "Убрать из группы", IsDestructive: true)
    ];

    public static IReadOnlyList<CatalogAction> ForIngredient(Ingredient ingredient) =>
    [
        new(CatalogActionKeys.Edit, "Редактировать"),
        Availability(ingredient.IsAvailable),
        new(CatalogActionKeys.Delete, "Удалить ингредиент", IsDestructive: true)
    ];

    /// <summary>Reads as the next thing you would do, not as the current state.</summary>
    private static CatalogAction Availability(bool isAvailable) => isAvailable
        ? new CatalogAction(CatalogActionKeys.ToggleAvailability, "Снять с продажи")
        : new CatalogAction(CatalogActionKeys.ToggleAvailability, "Вернуть в продажу");
}
