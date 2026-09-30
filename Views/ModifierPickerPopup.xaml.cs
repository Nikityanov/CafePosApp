using CafePos.Core.Models;
using CommunityToolkit.Maui.Views;

namespace CafePosApp.Views;

public partial class ModifierPickerPopup : Popup<string?>
{
    public ModifierPickerPopup(ModifierGroup group)
    {
        InitializeComponent();
        TitleLabel.Text = group.Name;
        foreach (var option in group.Options)
        {
            var button = new Button
            {
                Text = option.IsAvailable ? option.Name : $"{option.Name} (нет)",
                HorizontalOptions = LayoutOptions.Fill,
                IsEnabled = option.IsAvailable,
                Opacity = option.IsAvailable ? 1.0 : 0.45
            };
            if (option.IsAvailable)
                button.Clicked += async (_, _) => await CloseAsync(option.Name);
            OptionsLayout.Add(button);
        }
    }
}
