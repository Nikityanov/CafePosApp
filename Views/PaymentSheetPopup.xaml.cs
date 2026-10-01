using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

/// <summary>
/// The payment bottom sheet: what is owed, a keypad to enter the amount in whole rubles, a
/// cash/card choice, and a confirm button. Returns the declared payment, or <c>null</c> when the
/// sheet is dismissed.
/// </summary>
/// <remarks>
/// Reuses the measured fixes from <see cref="CatalogActionSheetPopup"/>: the ScrollView carries
/// <c>VerticalOptions="Start"</c> (without it the sheet stretched to its height cap), the width is
/// forced in code because the toolkit rewrites <c>HorizontalOptions.Fill</c> to <c>Center</c>, and
/// the dim comes from <c>PopupOptions.PageOverlayColor</c> in <see cref="Services.PaymentSheet"/>
/// because the toolkit insets popup content by 15dp per side.
/// <para>
/// The keypad is buttons rather than an <c>Entry</c> on purpose: the owner chose this because the
/// OS keyboard covers half the screen and is slow to bring up, and an <c>Entry</c> raises it on
/// every tap. Amounts are entered in whole rubles — there is no decimal key.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class PaymentSheetPopup : Popup<PaymentSheetResult?>
{
    /// <summary>Whole rubles. A coffee-shop check never approaches this; it only stops a stray keypress from printing a 20-digit number.</summary>
    private const long MaxEnteredRubles = 999_999;

    private readonly PaymentSheetRequest request;

    private long enteredRubles;
    private PaymentMethod selectedMethod = PaymentMethod.Cash;

    public PaymentSheetPopup(PaymentSheetRequest request)
    {
        InitializeComponent();
        this.request = request;

        // Full-width bottom sheet. The toolkit converts HorizontalOptions.Fill back to Center, so
        // the width is set explicitly instead — the same fix as CatalogActionSheetPopup. The window
        // width is used (not the physical display width) so the sheet fits in split-screen windows.
        SizeToWindow();
        this.SizeChanged += (_, _) => SizeToWindow();

        // Start below the screen edge; Opened slides the sheet up into place.
        TranslationY = 1000;

        TitleLabel.Text = request.Title;
        AmountDueLabel.Text = $"К оплате: {TextFormat.Money(request.AmountDue)}";

        AlreadyPaidLabel.IsVisible = request.AlreadyPaid > 0;
        if (request.AlreadyPaid > 0)
        {
            AlreadyPaidLabel.Text = $"Уже оплачено: {TextFormat.Money(request.AlreadyPaid)} · доплата";
        }

        Refresh();
        RefreshMethod();

        Opened += (_, _) =>
        {
            SizeToWindow();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    private void OnKeyClicked(object? sender, EventArgs e)
    {
        var key = (sender as Button)?.CommandParameter?.ToString();
        switch (key)
        {
            case "backspace":
                // Integer division: drop the last digit.
                enteredRubles = enteredRubles / 10;
                break;
            case "clear":
                enteredRubles = 0;
                break;
            default:
                if (int.TryParse(key, out var digit) && enteredRubles <= MaxEnteredRubles / 10)
                {
                    enteredRubles = enteredRubles * 10 + digit;
                }
                break;
        }

        Refresh();
    }

    private void OnMethodClicked(object? sender, EventArgs e)
    {
        selectedMethod = (sender as Button)?.CommandParameter?.ToString() == "card"
            ? PaymentMethod.Card
            : PaymentMethod.Cash;
        RefreshMethod();
    }

    private void OnConfirmClicked(object? sender, EventArgs e)
    {
        // The entered amount is passed as typed; the service clamps it to the balance. The change
        // the operator hands back is entered − balance, which is what ChangeLabel showed.
        _ = CloseAsync(new PaymentSheetResult(enteredRubles, selectedMethod));
    }

    /// <summary>
    /// Redraws the entered amount, the change line and the confirm button. Called after every key.
    /// </summary>
    private void Refresh()
    {
        EnteredLabel.Text = TextFormat.Money(enteredRubles);

        var change = enteredRubles - request.AmountDue;
        ChangeLabel.IsVisible = change > 0;
        if (change > 0)
        {
            ChangeLabel.Text = $"Сдача: {TextFormat.Money(change)}";
        }

        ConfirmButton.IsEnabled = enteredRubles > 0;
    }

    /// <summary>Fills the chosen method button and tones the other, so the active choice is visible.</summary>
    private void RefreshMethod()
    {
        CashButton.Style = selectedMethod == PaymentMethod.Cash
            ? (Style)Application.Current!.Resources["PaymentMethodActive"]!
            : (Style)Application.Current!.Resources["PaymentMethodButton"]!;
        CardButton.Style = selectedMethod == PaymentMethod.Card
            ? (Style)Application.Current!.Resources["PaymentMethodActive"]!
            : (Style)Application.Current!.Resources["PaymentMethodButton"]!;
    }

    /// <summary>
    /// Matches the sheet to the window it is shown over, and lifts its content above the system
    /// navigation bar.
    /// </summary>
    /// <remarks>
    /// The height is only a cap, never a target: <c>MaximumHeightRequest</c> stops the ScrollView
    /// growing, and the ScrollView's own <c>VerticalOptions="Start"</c> is what makes it stop at its
    /// content.
    /// <para>
    /// The bottom inset is the real system navigation-bar height read from the platform, not a
    /// hand-picked number: the sheet is pinned to the window's bottom edge, and on an edge-to-edge
    /// Android window the navigation bar covers that edge, so the confirm button would sit under it
    /// without this. Read from the <c>navigation_bar_height</c> resource — the same one MAUI's own
    /// platform code reads — so it tracks the actual bar. Zero on platforms with no bottom bar.
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

            // Cap the sheet at 90% of the window height so the handle stays on a short landscape window.
            var height = window?.Height ?? 0;
            if (height > 0 && SheetScroll != null)
            {
                SheetScroll.MaximumHeightRequest = height * 0.9;
            }

            // Lift the content above the system navigation bar. The sheet's own 16dp bottom padding
            // is kept on top of the inset, so the confirm button clears the bar with room to spare.
            if (SheetContent != null)
            {
                var inset = GetBottomInset();
                SheetContent.Padding = new Thickness(16, 8, 16, 16 + inset);
            }
        }
        catch (Exception exception)
        {
            // Leave the default size if the window info is unavailable — but say so, rather than
            // discarding the reason.
            AppLog.Exception("PaymentSheetPopup.SizeToWindow", exception);
        }
    }

    /// <summary>
    /// The height of the system navigation bar in device-independent pixels, or 0 when there is
    /// none. Android-only: the other platforms either have no bottom bar or inset their content
    /// themselves.
    /// </summary>
    private static double GetBottomInset()
    {
        try
        {
#if ANDROID
            var resources = Platform.CurrentActivity?.Resources;
            if (resources is not null)
            {
                // The same resource MAUI's own platform code reads for the navigation bar height.
                var resourceId = resources.GetIdentifier("navigation_bar_height", "dimen", "android");
                if (resourceId > 0)
                {
                    var bottomPx = resources.GetDimensionPixelSize(resourceId);
                    if (bottomPx > 0)
                    {
                        return bottomPx / resources.DisplayMetrics.Density;
                    }
                }
            }
#endif
            return 0;
        }
        catch
        {
            return 0;
        }
    }
}
