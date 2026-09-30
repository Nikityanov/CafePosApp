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
                Text = $"{name} — {draft.TotalQuantity} поз., {draft.Total:F0} ₽ ({draft.UpdatedAt.ToLocalTime():HH:mm})",
                HorizontalOptions = LayoutOptions.Fill
            };
            var id = draft.Id.ToString();
            button.Clicked += async (_, _) => await CloseAsync(id);
            OptionsLayout.Add(button);
        }
    }
}
