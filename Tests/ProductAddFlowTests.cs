using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

/// <summary>
/// Разбор путей отмены при добавлении блюда в корзину: закрытие листа модификаторов,
/// недоступные варианты и закрытие листа вариантов. Логика вынесена в
/// <see cref="ProductAddFlow"/>, поэтому проверяется без MAUI — сам ViewModel остаётся тонким.
/// </summary>
public class ProductAddFlowTests
{
    private static ProductVariant Variant(string name, decimal price, bool isAvailable = true, int sortOrder = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Price = price,
        IsAvailable = isAvailable,
        SortOrder = sortOrder
    };

    private static Product Product(
        bool hasVariants,
        decimal price,
        ModifierGroup? modifierGroup = null,
        ProductVariant[]? variants = null)
    {
        var list = new List<ProductVariant>();
        if (variants is not null) list.AddRange(variants);

        return new Product
        {
            Id = Guid.NewGuid(),
            Name = "Латте",
            Price = price,
            IsAvailable = true,
            HasVariants = hasVariants,
            ModifierGroup = modifierGroup,
            Variants = list
        };
    }

    [Fact]
    public void Dismissing_the_modifier_sheet_aborts_the_whole_add()
    {
        var group = new ModifierGroup { Id = Guid.NewGuid(), Name = "Добавки" };
        var flow = ProductAddFlow.Start(Product(hasVariants: false, price: 220m, modifierGroup: group));

        // Путь 1: лист модификаторов был открыт, но закрыт без выбора.
        var outcome = flow.SubmitModifier(picked: null);

        Assert.Equal(AddToCartStepOutcome.Dismissed, outcome);
    }

    [Fact]
    public void A_product_without_a_modifier_group_skips_the_step_even_when_nothing_was_picked()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: false, price: 220m));

        // Обратная сторона пути 1: null здесь — не отмена, а «шаг не выполнялся».
        // Если убрать проверку ModifierGroup is not null, тест упадёт.
        Assert.Null(flow.ModifierGroup);
        Assert.Equal(AddToCartStepOutcome.Proceed, flow.SubmitModifier(picked: null));
    }

    [Fact]
    public void Picking_a_modifier_lets_the_flow_continue()
    {
        var group = new ModifierGroup { Id = Guid.NewGuid(), Name = "Добавки" };
        var flow = ProductAddFlow.Start(Product(hasVariants: false, price: 220m, modifierGroup: group));

        Assert.Equal(AddToCartStepOutcome.Proceed, flow.SubmitModifier(picked: "Сироп"));
    }

    [Fact]
    public void Dismissing_the_variant_sheet_aborts_the_add()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 0m, variants: [Variant("Большой", 350m)]));

        // Путь 3: лист вариантов открылся (доступный вариант есть) и был закрыт без выбора.
        Assert.True(flow.CanOfferVariantChoice);
        Assert.Equal(AddToCartStepOutcome.Dismissed, flow.SubmitVariant(picked: null));
    }

    [Fact]
    public void A_flagged_product_with_no_variants_at_all_cannot_be_offered()
    {
        // Флаг HasVariants стоит, а строк вариантов нет — каталог такое сохраняет.
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 0m));

        // Путь 2: блюдо с флагом, но без единого варианта. Не отмена, а отказ добавить.
        Assert.True(flow.RequiresVariantChoice);
        Assert.Empty(flow.AvailableVariants);
        Assert.False(flow.CanOfferVariantChoice);
    }

    [Fact]
    public void A_product_whose_variants_are_all_sold_out_cannot_be_offered()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 0m, variants:
        [
            Variant("Большой", 350m, isAvailable: false, sortOrder: 1),
            Variant("Средний", 300m, isAvailable: false, sortOrder: 2)
        ]));

        // Путь 2 во второй форме: варианты есть, но все распроданы — показать нечего.
        Assert.True(flow.RequiresVariantChoice);
        Assert.Empty(flow.AvailableVariants);
        Assert.False(flow.CanOfferVariantChoice);
        Assert.NotEmpty(ProductAddFlow.NoAvailableVariantsMessage);
    }

    [Fact]
    public void Only_available_variants_are_offered_in_sort_order()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 0m, variants:
        [
            Variant("Маленький", 200m, sortOrder: 1),
            Variant("Большой", 350m, sortOrder: 3),
            Variant("Средний", 300m, sortOrder: 2),
            Variant("Двойной", 400m, isAvailable: false, sortOrder: 0)
        ]));

        // Порядок вставки не совпадает с SortOrder, и один вариант распродан, поэтому тест
        // ломается и без фильтра IsAvailable, и без сортировки по SortOrder.
        Assert.Equal(3, flow.AvailableVariants.Count);
        Assert.Equal(
            new[] { "Маленький", "Средний", "Большой" },
            flow.AvailableVariants.Select(variant => variant.Name));
        Assert.True(flow.CanOfferVariantChoice);
    }

    [Fact]
    public void A_product_flagged_without_variants_skips_the_step_even_when_variant_rows_exist()
    {
        // Обратная сторона пути 2: строки вариантов есть, но флаг снят — шаг выполняться не должен.
        var flow = ProductAddFlow.Start(Product(hasVariants: false, price: 220m, variants: [Variant("Большой", 350m)]));

        Assert.False(flow.RequiresVariantChoice);
        Assert.Empty(flow.AvailableVariants);
        Assert.True(flow.CanOfferVariantChoice);
        Assert.Equal(220m, flow.ResolvePrice(variant: null));
    }

    [Fact]
    public void The_chosen_variant_price_replaces_the_product_price()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 220m, variants:
        [
            Variant("Большой", 350m, sortOrder: 1),
            Variant("Средний", 300m, sortOrder: 2)
        ]));

        // Цена варианта заменяет цену блюда, а не прибавляется к ней.
        Assert.Equal(350m, flow.ResolvePrice("Большой"));
        Assert.Equal(300m, flow.ResolvePrice("Средний"));
    }

    [Fact]
    public void Without_a_variant_the_product_price_is_charged()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: false, price: 220m));

        Assert.Equal(220m, flow.ResolvePrice(variant: null));
    }

    [Fact]
    public void An_unknown_variant_name_falls_back_to_the_product_price()
    {
        var flow = ProductAddFlow.Start(Product(hasVariants: true, price: 220m, variants: [Variant("Большой", 350m)]));

        // Лист вернул имя, которого нет в каталоге: лучше цена блюда, чем ноль в корзине.
        Assert.Equal(220m, flow.ResolvePrice("Такого нет"));
    }
}
