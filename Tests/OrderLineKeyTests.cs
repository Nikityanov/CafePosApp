using CafePos.Core.Common;

namespace CafePosApp.Tests;

/// <summary>
/// Ключ склейки строк корзины. Именно по нему решается, присоединится ли повторный тап к строке или
/// откроет новую, поэтому проверяется без ViewModel: чистая функция, у которой нет иных причин
/// разойтись у вызывающих.
/// </summary>
public class OrderLineKeyTests
{
    private static readonly Guid Latte = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Cappuccino = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Croissant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void The_same_tap_twice_gives_the_same_key()
    {
        // Базовый случай склейки: два тапа по одной и той же чашке должны попасть в одну строку.
        Assert.Equal(
            OrderLineKey.For(Latte, null, null),
            OrderLineKey.For(Latte, null, null));
    }

    [Fact]
    public void Another_dish_gives_another_key()
    {
        Assert.NotEqual(
            OrderLineKey.For(Latte, null, null),
            OrderLineKey.For(Cappuccino, null, null));
    }

    [Theory]
    [InlineData("Сироп", null)]
    [InlineData(null, "Большой")]
    public void A_modifier_or_a_variant_changes_the_key(string? modifier, string? variant)
    {
        // Без этого кассир продаёт две чашки в одну строку: количество суммируется, а цена у строки
        // одна, и в чеке не сойдётся.
        Assert.NotEqual(
            OrderLineKey.For(Latte, modifier, variant),
            OrderLineKey.For(Latte, null, null));
    }

    [Fact]
    public void A_modifier_and_a_variant_that_only_swapped_places_are_different_lines()
    {
        // Имена — свободный текст из каталога, поэтому «Сироп»/«Большой» и «Большой»/«Сироп» —
        // разные позиции, даже когда набор символов один.
        Assert.NotEqual(
            OrderLineKey.For(Latte, "Сироп", "Большой"),
            OrderLineKey.For(Latte, "Большой", "Сироп"));
    }

    [Fact]
    public void A_modifier_containing_the_separator_does_not_collide_with_a_second_one()
    {
        // Длина каждого имени входит в ключ. Без неё «АБ»+«» и «А»+«Б» дали бы одну строку на две
        // разные позиции по разным ценам — и количество сложилось бы, а сумма нет.
        Assert.NotEqual(
            OrderLineKey.For(Latte, "АБ", string.Empty),
            OrderLineKey.For(Latte, "А", "Б"));
    }

    [Fact]
    public void A_bundle_and_a_plain_line_of_the_same_dish_are_different_lines()
    {
        // Состав входит в ключ, иначе комбо и обычная порция одной позиции склеились бы по цене,
        // которая у них разная.
        Assert.NotEqual(
            OrderLineKey.For(Latte, null, null),
            OrderLineKey.For(Latte, null, null, [(Cappuccino, 1)]));
    }

    [Fact]
    public void The_component_order_does_not_change_the_key()
    {
        // Ключевое требование: два одинаковых по смыслу комбо, набранные в разном порядке, должны
        // склеиться. Сортировка по ProductId делает подпись независимой от порядка; без неё два
        // одинаковых набора разъезжались бы по разным строкам, а разные — склеивались.
        var first = OrderLineKey.For(Latte, null, null, [(Cappuccino, 1), (Croissant, 1)]);
        var second = OrderLineKey.For(Latte, null, null, [(Croissant, 1), (Cappuccino, 1)]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_component_order_does_not_change_the_key_even_with_three_slots()
    {
        var third = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var components = new[] { (Latte, 1), (Cappuccino, 2), (Croissant, 1), (third, 1) };

        var forwards = OrderLineKey.For(Croissant, null, null, components);
        var backwards = OrderLineKey.For(Croissant, null, null, components.Reverse());

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void Two_slots_of_the_same_dish_are_ordered_by_quantity_too()
    {
        // Одного ProductId для сортировки мало: две ячейки одного блюда с разной кратностью
        // сравниваются как равные, и стабильность LINQ не гарантирована. Без добивки по количеству
        // (1,2) и (2,1) сложились бы в разные ключи и один набор распался бы на две строки.
        var one = OrderLineKey.For(Croissant, null, null, [(Latte, 1), (Latte, 2)]);
        var two = OrderLineKey.For(Croissant, null, null, [(Latte, 2), (Latte, 1)]);

        Assert.Equal(one, two);
    }

    [Fact]
    public void A_different_multiplicity_in_a_slot_is_a_different_bundle()
    {
        // «Эспрессо ×1» и «эспрессо ×2» — разные наборы и разные цены, поэтому разные строки.
        Assert.NotEqual(
            OrderLineKey.For(Croissant, null, null, [(Latte, 1)]),
            OrderLineKey.For(Croissant, null, null, [(Latte, 2)]));
    }

    [Fact]
    public void A_different_set_of_slots_is_a_different_bundle()
    {
        Assert.NotEqual(
            OrderLineKey.For(Croissant, null, null, [(Latte, 1)]),
            OrderLineKey.For(Croissant, null, null, [(Cappuccino, 1)]));
    }

    [Fact]
    public void An_empty_component_list_means_a_plain_line()
    {
        // Пустой список и null — одно и то же состояние («не комбо»). Если бы они давали разные
        // ключи, один и тот же обычный напиток распался бы на две строки в зависимости от того,
        // каким списком его собрали.
        Assert.Equal(
            OrderLineKey.For(Latte, null, null),
            OrderLineKey.For(Latte, null, null, []));
    }

    [Fact]
    public void The_component_signature_cannot_be_mistaken_for_a_variant_name()
    {
        // Состав дописан в ключ как отдельная часть, а не склеен с именем: иначе подпись слота,
        // начинающаяся с '|', изобразила бы модификатор.
        var bundle = OrderLineKey.For(Latte, null, null, [(Croissant, 1)]);
        var sneaky = OrderLineKey.For(Latte, null, null + $"|+{Croissant:N}x1");

        Assert.NotEqual(bundle, sneaky);
    }
}
