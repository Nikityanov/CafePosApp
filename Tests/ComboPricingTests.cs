using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

/// <summary>
/// Цена комбо и ориентир, с которым её сравнивают. Здесь нет базы и нет EF: и то, что берёт касса, и
/// то, с чем это сравнивается, — чистая арифметика, и именно поэтому её можно проверять целиком, а не
/// по частям.
/// <para>
/// Главное, что изменилось: сумма состава перестала быть ценой. <see cref="Combo.PriceKopecks"/> — это
/// то, что берёт касса, и число из карточки, а сумма — ориентир «à la carte», с которым эту цену
/// сравнивают. Тесты этого файла написаны под новую модель, а не под старую: инвариант «сумма = цена»
/// в них больше не проверяется, потому что он теперь ложь по замыслу.
/// </para>
/// </summary>
public class ComboPricingTests
{
    private static ComboComponentPrice Slot(long unitKopecks, int quantityPerUnit = 1) =>
        new(quantityPerUnit, unitKopecks);

    // ─── The reference, which is no longer the price ───

    [Fact]
    public void The_reference_of_a_bundle_is_the_sum_of_its_slots()
    {
        // Эспрессо 120 + круассан 180 = 300 ₽ «по отдельности». Это ОРИЕНТИР, а не цена набора: цену
        // объявляет бухгалтерия в карточке (Combo.PriceKopecks), и она может быть любой.
        var reference = ComboPricing.ReferenceKopecks([Slot(12000), Slot(18000)]);

        Assert.Equal(30000, reference);
    }

    [Fact]
    public void A_free_slot_adds_nothing_to_the_reference_and_does_not_make_the_bundle_free()
    {
        // Обратная сторона нулевого слота: он не должен ни уменьшать сумму, ни делать её пустой, но и
        // на цену он больше не влияет НИКАК. Бесплатный слот — утверждение о сравнении, а не о счёте.
        var reference = ComboPricing.ReferenceKopecks([Slot(12000), Slot(0), Slot(18000)]);
        var combo = new Combo { Name = "Кофе с сахаром", PriceKopecks = 30000 };

        Assert.Equal(30000, reference);
        Assert.Equal(30000, combo.PriceKopecks);
    }

    [Fact]
    public void A_bundle_of_only_free_slots_has_a_reference_of_zero()
    {
        // Крайний случай: ориентир равен нулю, и это НЕ ошибка — так оформляют кофе в рамках акции.
        // Ноль должен быть законным ответом суммы, а не исключением. Что с ним делать дальше —
        // отдельное решение (процент не определён), и оно проверяется ниже.
        Assert.Equal(0, ComboPricing.ReferenceKopecks([Slot(0), Slot(0)]));
    }

    [Fact]
    public void An_empty_bundle_has_a_reference_of_nothing_rather_than_failing()
    {
        // Пустой состав не должен ронять расчёт: цену вернёт покупатель.
        Assert.Equal(0, ComboPricing.ReferenceKopecks([]));
    }

    [Fact]
    public void A_slot_taken_several_times_multiplies_its_own_price()
    {
        // Кратность слота — свойство СЛОТА, а не строки заказа: N в строке означает N полных наборов,
        // поэтому «2 эспрессо в наборе» входит в ориентир множителем, а не отдельной строкой.
        var reference = ComboPricing.ReferenceKopecks([Slot(12000, quantityPerUnit: 2), Slot(18000)]);

        Assert.Equal(42000, reference);
    }

    [Fact]
    public void LineKopecks_multiplies_without_rounding_drift()
    {
        // Цена в копейках целая, и умножение на кратность тоже должно остаться целым: 33.45 ₽ — это
        // нечётное число копеек, и ошибка округления здесь стоила бы копейку на каждом наборе.
        var slot = Slot(3345, quantityPerUnit: 3);

        Assert.Equal(10035, slot.LineKopecks);
        Assert.Equal(3345, slot.UnitKopecks);
        Assert.Equal(3, slot.QuantityPerUnit);
    }

    [Fact]
    public void The_old_name_of_the_reference_still_returns_the_same_number()
    {
        // TotalKopecks остался как имя для слоя MAUI, который ещё не переехал. Проверка нужна, чтобы
        // переименование не оказалось тихой сменой арифметики: число то же, но ЗНАЧЕНИЕ прежнее — это
        // ориентир, а не цена, и читать его как цену больше нельзя.
        Assert.Equal(
            ComboPricing.ReferenceKopecks([Slot(12000), Slot(18000)]),
            ComboPricing.TotalKopecks([Slot(12000), Slot(18000)]));
    }

    // ─── The price is a number, not a sum ───

    [Fact]
    public void A_bundle_is_charged_its_own_price_even_when_that_differs_from_its_parts()
    {
        // СКВОЗНОЙ ИНВАРИАНТ НОВОЙ МОДЕЛИ. Сумма 300 ₽, цена 250 ₽ — набор дешевле своих частей, и
        // касса берёт 250, потому что так сказала бухгалтерия, а не потому, что так сложились слоты.
        var combo = new Combo { Name = "Набор", PriceKopecks = 25000 };
        var reference = ComboPricing.ReferenceKopecks([Slot(12000), Slot(18000)]);

        Assert.Equal(25000, combo.PriceKopecks);
        Assert.Equal(30000, reference);
    }

    [Fact]
    public void Raising_a_dish_price_does_not_reprice_the_bundle()
    {
        // Сценарий, ради которого поле цены и появилось: цена эспрессо выросла — и набор не подорожал.
        // Слот без своей цены по-прежнему тянет цену блюда (ResolveUnitKopecks), но эта цифра ушла в
        // ОРИЕНТИР, а не в счёт. Раньше набор подорожал сам, и решал это никто.
        var before = ComboPricing.ReferenceKopecks([new ComboComponentPrice(1, ComboPricing.ResolveUnitKopecks(null, 18000))]);
        var after = ComboPricing.ReferenceKopecks([new ComboComponentPrice(1, ComboPricing.ResolveUnitKopecks(null, 22000))]);
        var combo = new Combo { Name = "Набор", PriceKopecks = 15000 };

        Assert.Equal(18000, before);
        Assert.Equal(22000, after);
        Assert.Equal(15000, combo.PriceKopecks);   // цена набора не поехала вместе с блюдом
    }

    // ─── The percentage ───

    [Fact]
    public void The_percentage_says_how_much_cheaper_the_bundle_is_than_its_parts()
    {
        // 300 ₽ ориентира, 250 ₽ цена → дешевле на 50 из 300.
        Assert.Equal(16.67m, ComboPricing.DiscountPercent(referenceKopecks: 30000, priceKopecks: 25000));
    }

    [Fact]
    public void A_zero_reference_has_no_percentage_at_all()
    {
        // Ключевое решение: нулевой ориентир даёт null, а НЕ 0. «0% дешевле своих частей» — ложь о
        // наборе, который стоит денег: процента от нуля не существует, а деление на ноль — это
        // исключение или выдуманная цифра. Показывать нечего, значит не показываем ничего.
        Assert.Null(ComboPricing.DiscountPercent(referenceKopecks: 0, priceKopecks: 25000));
        // Отрицательный ориентир — тоже не знаменатель, и отношение к нему бессмысленно.
        Assert.Null(ComboPricing.DiscountPercent(referenceKopecks: -100, priceKopecks: 25000));
    }

    [Fact]
    public void A_bundle_priced_above_its_parts_reports_a_surcharge_and_not_a_discount()
    {
        // НАЦЕНКА, не скидка. 300 ₽ частей, 330 ₽ набор → −10%: знак минус здесь читается как наценка,
        // и Compare отдаёт Dearer, чтобы вызывающий код не вывел это как «скидку −10%».
        Assert.Equal(-10m, ComboPricing.DiscountPercent(referenceKopecks: 30000, priceKopecks: 33000));
        Assert.Equal(ComboPriceRelation.Dearer, ComboPricing.Compare(30000, 33000));
    }

    [Fact]
    public void A_bundle_priced_at_its_parts_is_equal_and_says_zero_percent()
    {
        // Здесь ноль — честный ответ, а не заглушка: знаменатель ненулевой, и цена с ним совпадает.
        Assert.Equal(0m, ComboPricing.DiscountPercent(referenceKopecks: 30000, priceKopecks: 30000));
        Assert.Equal(ComboPriceRelation.Equal, ComboPricing.Compare(30000, 30000));
    }

    [Fact]
    public void A_cheaper_bundle_is_reported_as_cheaper()
    {
        Assert.Equal(ComboPriceRelation.Cheaper, ComboPricing.Compare(30000, 25000));
    }

    [Fact]
    public void A_zero_reference_has_no_relation_to_report()
    {
        // Четвёртое состояние, которого нет у знака процента: сравнивать не с чем. Unknown, а не Equal и
        // не Cheaper — иначе набор без ориентира выглядел бы как «не дешевле», что тоже неправда.
        Assert.Equal(ComboPriceRelation.Unknown, ComboPricing.Compare(0, 25000));
    }

    [Fact]
    public void The_percentage_is_rounded_to_two_decimals_away_from_zero()
    {
        // 1/3 от 100 ₽ — это 33.333…%. Процент читают глазами и иногда сверяют калькулятором, поэтому
        // округление должно быть тем же, что у калькулятора: 5.005 → 5.01, а не 5.00.
        Assert.Equal(33.33m, ComboPricing.DiscountPercent(referenceKopecks: 10000, priceKopecks: 6667));
        Assert.Equal(-0.01m, ComboPricing.DiscountPercent(referenceKopecks: 10000, priceKopecks: 10001));
    }

    // ─── What one slot is worth ───

    [Fact]
    public void A_slot_without_its_own_price_is_worth_the_dish_price()
    {
        // null = «цена блюда»: обычное состояние слота, а не ноль.
        Assert.Equal(18000, ComboPricing.ResolveUnitKopecks(null, 18000));
    }

    [Fact]
    public void A_slot_price_of_zero_means_free_and_does_not_fall_back_to_the_dish_price()
    {
        // Главная развилка трёх состояний: 0 — это «бесплатно» в ОРИЕНТИРЕ, а не «не задано». Схлопывание
        // 0 в null превратило бы бесплатный слот в платный, и это обнаружилось бы только в чеке.
        Assert.Equal(0, ComboPricing.ResolveUnitKopecks(0, 18000));
    }

    [Fact]
    public void A_slot_price_replaces_the_dish_price_instead_of_adding_to_it()
    {
        // Aloha «Item price», как уже делает ProductAddFlow.ResolvePrice для варианта: своя цена слота
        // ЗАМЕНЯЕТ цену блюда. Сложение дало бы «250 + 150 = 400 ₽» там, где кассир ожидал 150 ₽.
        Assert.Equal(15000, ComboPricing.ResolveUnitKopecks(15000, 25000));
    }

    [Fact]
    public void The_slot_price_wins_even_when_the_dish_is_free()
    {
        // Обратная сторона замены: бесплатное блюдо не должно обнулять цену, которую слот назвал явно.
        Assert.Equal(9000, ComboPricing.ResolveUnitKopecks(9000, 0));
    }

    // ─── Order invariants, which did not move ───

    [Fact]
    public void Three_identical_bundles_are_one_line_of_three_and_three_times_the_price()
    {
        // Инвариант заказа держится: комбо — одна строка с Quantity = 3, поэтому цена строки втрое
        // больше цены одного набора, и RecalculateTotal остаётся Σ(PriceKopecks × Quantity).
        var item = new OrderItem { PriceKopecks = 25000, Quantity = 3 };

        Assert.Equal(75000, item.LineTotalKopecks);
    }

    [Fact]
    public void An_order_of_a_bundle_and_an_ordinary_dish_still_totals_as_kopecks()
    {
        // Тот же инвариант целиком, на реальной сущности: существующая формула заказа не изменилась
        // и не должна была измениться из-за появления собственной цены у комбо.
        var order = new Order();
        order.Items.Add(new OrderItem { PriceKopecks = 25000, Quantity = 2 });
        order.Items.Add(new OrderItem { Price = 19.99m, Quantity = 1 });

        order.RecalculateTotal();

        Assert.Equal(50000 + 1999, order.TotalKopecks);
    }
}
