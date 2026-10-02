using System.Globalization;
using CafePos.Core.Common;
using CafePos.Core.Models;
using CommunityToolkit.Maui.Views;

namespace CafePosApp.Views;

public partial class VariantPickerPopup : Popup<string?>
{
    public VariantPickerPopup(string productName, List<ProductVariant> variants)
    {
        InitializeComponent();
        TitleLabel.Text = productName;
        foreach (var variant in variants.Where(v => v.IsAvailable).OrderBy(v => v.SortOrder))
        {
            var button = new Button
            {
                // Whole units as before, but the sign comes from the selected currency instead of
                // being written into the format string. F0 rather than the currency's own digit
                // count: this is a compact one-line option in a popup, not a printed total.
                Text = $"{variant.Name} — {Money.Round(variant.Price).ToString("F0", CultureInfo.CurrentCulture)} {Currencies.Default.Symbol}",
                HorizontalOptions = LayoutOptions.Fill,
            };
            var name = variant.Name;
            button.Clicked += async (_, _) => await CloseAsync(name);
            OptionsLayout.Add(button);
        }
    }
}
