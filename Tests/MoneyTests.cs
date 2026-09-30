using CafePos.Core.Common;
using CafePos.Core.Models;

namespace CafePosApp.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData(19.99, 1999)]
    [InlineData(0.01, 1)]
    [InlineData(100.00, 10000)]
    [InlineData(0.575, 58)]   // commercial rounding: away from zero
    [InlineData(-0.575, -58)]
    public void ToKopecks_converts_rubles_to_integer_kopecks(double rubles, long expected) =>
        Assert.Equal(expected, Money.ToKopecks((decimal)rubles));

    [Fact]
    public void FromKopecks_round_trips() =>
        Assert.Equal(19.99m, Money.FromKopecks(1999));

    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(2.675, 2.68)] // away from zero, not banker's rounding
    public void Round_is_commercial_away_from_zero(double input, double expected) =>
        Assert.Equal((decimal)expected, Money.Round((decimal)input));

    [Fact]
    public void ApplyPercent_marks_up_and_down()
    {
        Assert.Equal(110m, Money.ApplyPercent(100m, 10m));
        Assert.Equal(169.96m, Money.ApplyPercent(199.95m, -15m));
    }

    [Fact]
    public void Product_price_is_stored_as_kopecks()
    {
        var product = new Product { Price = 249.90m };
        Assert.Equal(24990, product.PriceKopecks);
        Assert.Equal(249.90m, product.Price);
    }

    [Fact]
    public void Order_total_is_an_integer_sum_of_line_kopecks()
    {
        var order = new Order();
        order.Items.Add(new OrderItem { Price = 19.99m, Quantity = 3 });  // 1999 * 3
        order.Items.Add(new OrderItem { Price = 0.05m, Quantity = 1 });   // + 5
        order.RecalculateTotal();
        Assert.Equal(6002, order.TotalKopecks);
        Assert.Equal(60.02m, order.TotalPrice);
    }
}
