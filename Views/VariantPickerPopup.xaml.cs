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
                Text = $"{variant.Name} — {variant.Price:F0} ₽",
                HorizontalOptions = LayoutOptions.Fill,
            };
            var name = variant.Name;
            button.Clicked += async (_, _) => await CloseAsync(name);
            OptionsLayout.Add(button);
        }
    }
}
