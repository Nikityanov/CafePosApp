using System.Globalization;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CommunityToolkit.Maui.Views;

namespace CafePosApp.Views;

/// <summary>Lists the parked carts; the result is the chosen draft id (string for the popup generic).</summary>
public partial class DraftPickerPopup : Popup<string?>
{
    public DraftPickerPopup(IReadOnlyList<DraftOrder> drafts)
    {
        InitializeComponent();
        TitleLabel.Text = "Отложенные заказы";
        foreach (var draft in drafts.OrderByDescending(item => item.UpdatedAt))
        {
            var name = string.IsNullOrWhiteSpace(draft.Name) ? "Без названия" : draft.Name;
            var button = new Button
            {
                // Whole units as before; the sign follows the selected currency.
                Text = $"{name} — {draft.TotalQuantity} поз., " +
                       $"{Money.Round(draft.Total).ToString("F0", CultureInfo.CurrentCulture)} {Currencies.Default.Symbol} " +
                       $"({draft.UpdatedAt.ToLocalTime():HH:mm})",
                HorizontalOptions = LayoutOptions.Fill
            };
            var id = draft.Id.ToString();
            button.Clicked += async (_, _) => await CloseAsync(id);
            OptionsLayout.Add(button);
        }
    }
}
