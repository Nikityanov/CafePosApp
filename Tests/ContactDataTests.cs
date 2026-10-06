using CafePos.Core.Data;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
using Microsoft.Data.Sqlite;

namespace CafePosApp.Tests;

/// <summary>
/// Контакты: телефон привязан к типу заказа и стирается по сроку. Обе половины проверяются против
/// настоящей базы, потому что обе — про то, что РЕАЛЬНО оказалось в строке заказа, а не про то, что
/// метод вернул.
/// </summary>
public class ContactDataTests
{
    private static async Task<Product> LatteAsync(TestHost.Host host)
    {
        var catalog = host.Get<ICatalogService>();
        var latte = new Product { Id = Guid.NewGuid(), Name = "Латте", Price = 220m, IsAvailable = true };
        await catalog.SaveProductAsync(latte);
        return latte;
    }

    private static CheckoutLine Line(Product latte) => new(latte.Id, latte.Name, 220m, 1);

    [Fact]
    public async Task A_takeaway_phone_is_stored_normalised()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.Takeaway, "8 (916) 123-45-67"));

        Assert.Equal("+79161234567", order.CustomerPhone);
        Assert.Equal(OrderType.Takeaway, order.OrderType);

        var reloaded = await host.Get<IOrderService>().GetOrderAsync(order.Id);
        Assert.Equal("+79161234567", reloaded!.CustomerPhone);
    }

    [Fact]
    public async Task A_counter_service_phone_never_reaches_the_database()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        // Главное правило фичи. 152-ФЗ ст. 6(1)(5) разрешает хранить телефон только там, где он нужен
        // для исполнения договора; счётчик КоАП 13.11 ч.12 — количество СТРОК, поэтому значение
        // отбрасывается на границе сервиса, а не удаляется постфактум.
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.CounterService, "8 (916) 123-45-67"));

        Assert.Null(order.CustomerPhone);
        Assert.Equal(OrderType.CounterService, order.OrderType);

        var reloaded = await host.Get<IOrderService>().GetOrderAsync(order.Id);
        Assert.Null(reloaded!.CustomerPhone);
    }

    [Fact]
    public async Task A_counter_service_phone_is_not_even_validated()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        // Строка, которую нормализация отвергла бы, НЕ должна останавливать продажу: она всё равно
        // не будет записана, и отказ из-за текста, который выбрасывается, — это отказ без причины.
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.CounterService, "не телефон"));

        Assert.Null(order.CustomerPhone);
    }

    [Fact]
    public async Task A_takeaway_phone_that_is_not_a_number_stops_the_sale()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync(
                [Line(latte)],
                new OrderDetailsIntent(OrderType.Takeaway, "8-916-Иван-45-67")));

        // Сообщение называет проблему, а не просто отказывает: оператор должен понять, что не так.
        Assert.Contains("Телефон", failure.Message);
        Assert.Empty(await host.Get<IOrderService>().GetActiveOrdersAsync());
    }

    [Fact]
    public async Task A_takeaway_phone_that_is_too_short_stops_the_sale()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        // Разбирается, но номер неполный. Хранить «+71234» на фискальном чеке хуже, чем не принять
        // заказ, и молча обрезать или дополнить его нельзя.
        var failure = await Assert.ThrowsAsync<ValidationFailureException>(
            () => host.Get<ICheckoutService>().CheckoutAsync(
                [Line(latte)],
                new OrderDetailsIntent(OrderType.Takeaway, "1234")));

        Assert.Contains("не полный номер", failure.Message);
    }

    [Fact]
    public async Task A_takeaway_order_with_no_phone_at_all_is_a_normal_sale()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        // Клиент вправе не дать номер. Это НЕ ошибка: пустой телефон — нормальное состояние, и
        // исключение здесь заставило бы вызывающего ловить его ради того, чтобы ничего не сделать.
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.Takeaway, CustomerPhone: null));

        Assert.Null(order.CustomerPhone);
        Assert.Equal(OrderType.Takeaway, order.OrderType);
    }

    [Fact]
    public async Task The_order_type_and_requested_time_are_carried_onto_the_order()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var requestedAt = DateTimeOffset.Parse("2026-10-04T18:30:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.Takeaway, null, requestedAt));

        Assert.Equal(requestedAt, order.RequestedAt);
        Assert.Equal(requestedAt, order.PromisedAt);   // просьба и обещание — одно поле здесь, null = ASAP
        Assert.True(order.IsScheduledAt(DateTimeOffset.Parse("2026-10-04T18:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // ─── Retention ───

    /// <summary>
    /// Backdates an order's completion so the 90-day threshold can be crossed without waiting 90 days
    /// and without a fake clock in the host (TestHost installs TimeProvider.System).
    /// </summary>
    private static async Task BackdateAsync(string databasePath, Guid orderId, int daysAgo)
    {
        var when = DateTimeOffset.UtcNow.AddDays(-daysAgo)
            .ToString("yyyy-MM-dd HH:mm:ss.fffffff+00:00", System.Globalization.CultureInfo.InvariantCulture);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE [Orders] SET [CompletedAt] = '{when}' WHERE [Id] = '{orderId.ToString().ToUpperInvariant()}'";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Order> TakeawayWithPhoneAsync(TestHost.Host host, Product latte)
    {
        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.Takeaway, "+79161234567"));

        var orders = host.Get<IOrderService>();
        await orders.AddPaymentAsync(order.Id, order.TotalPrice, PaymentMethod.Cash);
        await orders.AdvanceStatusAsync(order.Id);
        await orders.AdvanceStatusAsync(order.Id);
        return order;
    }

    [Fact]
    public async Task Purging_clears_an_old_phone_and_leaves_a_recent_one()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var old = await TakeawayWithPhoneAsync(host, latte);
        var recent = await TakeawayWithPhoneAsync(host, latte);
        await BackdateAsync(host.DatabasePath, old.Id, daysAgo: 120);

        var purged = await host.Get<IContactDataService>().PurgeAsync(TimeSpan.FromDays(90));

        Assert.Equal(1, purged);

        var orders = host.Get<IOrderService>();
        // Значение стёрто, строка заказа НЕ тронута: чек — фискальный документ, и удалять заказы ради
        // правила о хранении — значит поменять одну юридическую проблему на другую, худшую.
        var purgedOrder = await orders.GetOrderAsync(old.Id);
        Assert.Null(purgedOrder!.CustomerPhone);
        Assert.Equal(22000, purgedOrder.TotalKopecks);
        Assert.Equal("+79161234567", (await orders.GetOrderAsync(recent.Id))!.CustomerPhone);
    }

    [Fact]
    public async Task Purging_leaves_a_phone_on_an_order_that_is_still_being_cooked()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var order = await host.Get<ICheckoutService>().CheckoutAsync(
            [Line(latte)],
            new OrderDetailsIntent(OrderType.Takeaway, "+79161234567"));
        await BackdateAsync(host.DatabasePath, order.Id, daysAgo: 120);

        // Цель обработки ещё не наступила: заказ не выдан, и телефон — единственный способ сказать
        // клиенту, что он готов. Стирать его было бы добросовестностью не в ту сторону.
        Assert.Equal(0, await host.Get<IContactDataService>().PurgeAsync(TimeSpan.FromDays(1)));
        Assert.Equal("+79161234567", (await host.Get<IOrderService>().GetOrderAsync(order.Id))!.CustomerPhone);
    }

    [Fact]
    public async Task Purging_nothing_leaves_the_numbers_alone()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);
        await TakeawayWithPhoneAsync(host, latte);

        Assert.Equal(0, await host.Get<IContactDataService>().PurgeAsync(TimeSpan.FromDays(90)));
    }

    [Fact]
    public async Task A_zero_or_negative_retention_is_a_no_op()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);
        var order = await TakeawayWithPhoneAsync(host, latte);

        // Порог «стереть всё, что закрыто» — это не порог, а случайно нажатая кнопка, стирающая ПДн
        // всех заказов за всю историю. Ноль и отрицательное значение молча ничего не делают.
        Assert.Equal(0, await host.Get<IContactDataService>().PurgeAsync(TimeSpan.Zero));
        Assert.Equal(0, await host.Get<IContactDataService>().PurgeAsync(TimeSpan.FromDays(-1)));
        Assert.Equal("+79161234567", (await host.Get<IOrderService>().GetOrderAsync(order.Id))!.CustomerPhone);
    }

    [Fact]
    public async Task Closing_a_shift_purges_the_contact_data()
    {
        using var host = TestHost.Create();
        await host.Get<DatabaseBootstrapper>().InitializeAsync();
        await TestHost.OpenEmptyShiftAsync(host.Get<IOrderService>());
        var latte = await LatteAsync(host);

        var old = await TakeawayWithPhoneAsync(host, latte);
        await BackdateAsync(host.DatabasePath, old.Id, daysAgo: 120);

        // Удержание привязано к уже существующему расписанию — закрытию смены, которое оператор и так
        // делает каждый день. Планировщик фоновых задач на телефоне переживает перезапуск и может
        // молча пропустить запуск, а обещание о хранении, зависящее от того, сработал ли таймер, — это
        // не обещание.
        //
        // Пересчёт = 220 ₽: смена обязана закрываться по факту кассы, и наличные за набор уже лежат
        // в ней. Причина расхождения не нужна, потому что расхождения нет.
        await host.Get<IOrderService>().CloseShiftAsync(countedCashKopecks: 22000, discrepancyReason: null);

        Assert.Null((await host.Get<IOrderService>().GetOrderAsync(old.Id))!.CustomerPhone);
    }
}