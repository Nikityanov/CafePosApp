using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

/// <summary>One recorded payment, rendered as a single line on the details page.</summary>
/// <remarks>Почему так — `docs/decisions/order-details.md`</remarks>

public sealed class PaymentLine : ObservableObject
{
    private string text = string.Empty;
    private string amountText = string.Empty;

    /// <summary>The sentence: the method, the moment and any note. Never contains money.</summary>
    public string Text { get => text; set => SetProperty(ref text, value); }

    /// <summary>The amount in the active currency, signed for a refund. Bound by the row.</summary>
    public string AmountText { get => amountText; set => SetProperty(ref amountText, value); }

    public bool IsRefund { get; init; }

    /// <remarks>`docs/decisions/order-details.md`</remarks>

    public Color AmountColor => IsRefund
        ? PaletteAccess.Resolve("Danger", "DangerDark")
        : PaletteAccess.Resolve("Black", "White");

    /// <summary>Money leaving the till is the app's one negative-number case, so it is named once here.</summary>
    public static string SignedAmount(decimal amount, bool isRefund) =>
        isRefund ? $"−{TextFormat.Money(amount)}" : TextFormat.Money(amount);

    /// <summary>Re-raises <see cref="AmountText"/> after the currency setting changed.</summary>
    public void RefreshMoneyText() => OnPropertyChanged(nameof(AmountText));
}

