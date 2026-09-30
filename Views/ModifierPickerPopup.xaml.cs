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

        // Size to the content instead of reserving a fixed 520dp: the old
        // MaximumHeightRequest="520" was reported as the popup's desired height even with two
        // options, leaving ~700px of blank white below the buttons.
        //
        // HeightRequest must be explicit — a ScrollView inside a Popup does NOT auto-size to
        // its content, and leaving it at 0 collapsed the sheet to an empty white pill (both
        // variants below were measured on the emulator, not reasoned about). It must also be
        // generous: an earlier under-estimate clipped the cancel button.
        // Clamped to 520 so a long group scrolls.
        const double padding = 20 * 2;
        const double title = 30;      // 22pt bold label
        const double subtitle = 20;   // secondary label
        const double cancel = 48;     // secondary button
        const double spacing = 12 * 3;
        const double perOption = 52;  // 44dp button + 8dp gap
        const double maxHeight = 520;

        var needed = padding + title + subtitle + cancel + spacing + (group.Options.Count * perOption);
        Scroll.HeightRequest = Math.Min(maxHeight, needed);
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await CloseAsync(null);
}
