using CafePos.Core.Models;

namespace CafePos.Core.Services;

/// <summary>The data behind the catalogue row overflow menu.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>


/// <summary>Stable identifier the caller switches on; never shown to the operator. The button label. Renders the entry in the danger colour.</summary>

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

/// <summary>Builds the overflow menu contents for each catalogue row type.</summary>
/// <remarks>Почему так — `docs/decisions/schema.md`</remarks>

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
