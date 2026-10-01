using CafePos.Core.Services;
using CafePosApp.Controls;
using CafePosApp.Diagnostics;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

/// <summary>
/// The overflow menu of a catalogue row: the item name as a sheet title, then one full-width
/// labelled entry per action, pinned to the bottom edge of the screen.
/// </summary>
/// <remarks>
/// MAUI has no cross-platform context menu — there is no <c>ContextMenu</c> control, and the
/// UWP-era <c>MenuFlyout</c> is reachable only through the explicit
/// <c>IContextFlyoutElement.ContextFlyout</c> interface, so it cannot be declared in XAML. The row
/// actions therefore live in a sheet that opens on demand. Labels are spelled out instead of being
/// abbreviated to glyphs, so nothing depends on a hover caption — which a touch screen never had.
/// <para>
/// The sheet is presented as a bottom sheet: VerticalOptions is set to End in XAML, the width is
/// forced to the screen width in code (the toolkit's HorizontalOptions converter turns Fill back
/// into Center), and the rounded top corners come from PopupOptions.Shape in
/// <see cref="Services.CatalogActionSheet"/>. The toolkit shows its popup without animation, so
/// the slide-up is done here: the popup starts translated below the screen and the Opened event
/// eases it up.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class CatalogActionSheetPopup : Popup<string?>
{
    public CatalogActionSheetPopup(string title, IReadOnlyList<CatalogAction> actions)
    {
        InitializeComponent();

        // Full-width bottom sheet. The toolkit converts HorizontalOptions.Fill back to Center, so
        // the width is set explicitly instead. The window width is used (not the physical display
        // width) so the sheet fits in split-screen/freeform windows. This re-measures whenever
        // the popup is laid out again.
        SizeToWindow();
        this.SizeChanged += (_, _) => SizeToWindow();

        // Start below the screen edge; Opened slides the sheet up into place.
        TranslationY = 1000;

        TitleLabel.Text = title;
        TitleLabel.IsVisible = !string.IsNullOrWhiteSpace(title);

        var dividerAdded = false;
        foreach (var action in actions)
        {
            // Separate the destructive entries from the safe ones with a divider rather than an
            // outline, so a long list does not turn into a column of red buttons.
            if (action.IsDestructive && !dividerAdded)
            {
                dividerAdded = true;
                OptionsLayout.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = Application.Current?.RequestedTheme == AppTheme.Dark
                        ? Color.FromRgb(97, 97, 97)     // Gray700
                        : Color.FromRgb(224, 224, 224),  // Gray300
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            var button = new Button { Text = action.Title, HorizontalOptions = LayoutOptions.Fill };
            ResourceStyles.TryApply(button, action.IsDestructive ? "SheetActionDanger" : "SheetAction");

            var key = action.Key;
            button.Clicked += async (_, _) => await CloseAsync(key);
            OptionsLayout.Add(button);
        }

        Opened += (_, _) =>
        {
            SizeToWindow();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    /// <summary>
    /// Matches the sheet to the width and height of the window it is shown over.
    /// </summary>
    /// <remarks>
    /// The previous version read <see cref="DeviceDisplay"/>, which is the physical display size —
    /// wrong in split-screen/freeform and after a rotation. The window width and height are used
    /// instead.
    /// <para>
    /// The height is only a cap, never a target: <c>MaximumHeightRequest</c> stops the ScrollView
    /// growing, and the ScrollView's own VerticalOptions="Start" is what makes it stop at its
    /// content. Raising this cap alone will not make the sheet taller — that was the bug, where a
    /// 292dp sheet was stretched to the full 823dp cap and left 57% of itself empty.
    /// </para>
    /// </remarks>
    private void SizeToWindow()
    {
        try
        {
            var window = Application.Current?.Windows.FirstOrDefault();
            var width = window?.Width ?? 0;
            if (width <= 0)
                width = this.Width;
            if (width <= 0)
                width = DeviceDisplay.MainDisplayInfo.Width / DeviceDisplay.MainDisplayInfo.Density;
            if (width > 0) WidthRequest = width;

            // The sheet is VerticalOptions="End", so a MaximumHeightRequest taller than the
            // window pushes the handle off the top of a short landscape window. Cap it at 90%
            // of the window height so the handle stays on screen.
            var height = window?.Height ?? 0;
            if (height > 0 && SheetScroll != null)
            {
                SheetScroll.MaximumHeightRequest = height * 0.9;
            }
        }
        catch (Exception exception)
        {
            // Leave the default size if the window info is unavailable — but say so, rather than
            // discarding the reason.
            AppLog.Exception("CatalogActionSheetPopup.SizeToWindow", exception);
        }
    }
}
