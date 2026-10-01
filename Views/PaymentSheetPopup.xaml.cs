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
/// Serves BOTH directions of money: taking a payment and giving one back, told apart by
/// <see cref="PaymentSheetMode"/>. Sharing the sheet is deliberate - the keypad, the sizing, the
/// dimmed backdrop and the confirm flow were each measured once, and none of them care which way
/// the money moves. What does care is the wording, and every caption is rewritten for a refund
/// (see <see cref="ApplyMode"/>): the title, the amount caption, the confirm button, and whether
/// the cash/card row exists at all.
/// <para>
/// Reuses the measured fixes from <see cref="CatalogActionSheetPopup"/>: the ScrollView carries
/// <c>VerticalOptions="Start"</c> (without it the sheet stretched to its height cap), the width is
/// forced in code because the toolkit rewrites <c>HorizontalOptions.Fill</c> to <c>Center</c>, and
/// the dim comes from <c>PopupOptions.PageOverlayColor</c> in <see cref="Services.PaymentSheet"/>
/// because the toolkit insets popup content by 15dp per side.
/// </para>
/// <para>
/// The keypad is buttons rather than an <c>Entry</c> on purpose: the owner chose this because the
/// OS keyboard covers half the screen and is slow to bring up, and an <c>Entry</c> raises it on
/// every tap. Amounts are entered in whole rubles, so there is no decimal key.
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

    /// <summary>
    /// The most that may be keyed in on a refund, in whole rubles, floored.
    /// </summary>
    /// <remarks>
    /// Floored rather than rounded because a refund above the collected amount is not a thing the
    /// operator can mean: the cap is a promise about what the till still holds, and rounding 220.60
    /// up to 221 would offer to give back 40 kopecks the drawer never received. The domain clamps
    /// with the same floor, so the keypad and the ledger agree.
    /// </remarks>
    private long RefundCeilingRubles => (long)Math.Floor(request.AmountDue);

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
        ApplyMode(request);

        Opened += (_, _) =>
        {
            SizeToWindow();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    /// <summary>
    /// Rewrites the sheet for the direction the money is going, then draws the first frame.
    /// </summary>
    /// <remarks>
    /// The direction rewrites every caption, not just the confirm button. That is the whole reason
    /// the sheet knows which way the money is moving: «Оплата заказа» and «К оплате» shown over a
    /// return are not a cosmetic mismatch, they instruct the operator to do the opposite of what
    /// they came here to do — and they are the two strings on screen for the whole time they are
    /// deciding how much to key in.
    /// <para>
    /// The method row is hidden on a refund rather than merely ignored, and the reason is that a
    /// refund takes no method at all: the domain mirrors the payments it reverses so the drawer and
    /// the terminal each balance against what they were actually credited. A visible-but-ignored
    /// choice would be worse than no choice — the operator would reasonably believe the return was
    /// booked the way they picked it.
    /// </para>
    /// </remarks>
    private void ApplyMode(PaymentSheetRequest request)
    {
        if (request.IsRefund)
        {
            AmountDueLabel.Text = $"Вернуть можно: {TextFormat.Money(request.AmountDue)}";
            RefundNoteLabel.IsVisible = true;
            RefundNoteLabel.Text = "Деньги уходят из кассы покупателю. Способ возврата не выбирается: "
                                 + "он совпадает с тем, как заказ принимали.";
            ConfirmButton.Text = "Вернуть оплату";
            SemanticProperties.SetDescription(ConfirmButton, "Вернуть оплату покупателю");
            MethodGrid.IsVisible = false;
        }
        else
        {
            AmountDueLabel.Text = $"К оплате: {TextFormat.Money(request.AmountDue)}";
        }

        // "Already settled in this direction": money collected on a top-up, money returned on a
        // partial refund. Without this line a second refund reads as a second refund of the FULL
        // original amount rather than of what is left, which is how an operator over-returns.
        AlreadyPaidLabel.IsVisible = request.AlreadyPaid > 0;
        if (request.AlreadyPaid > 0)
        {
            AlreadyPaidLabel.Text = request.IsRefund
                ? $"Уже возвращено: {TextFormat.Money(request.AlreadyPaid)}"
                : $"Уже оплачено: {TextFormat.Money(request.AlreadyPaid)} · доплата";
        }

        Refresh();
        if (!request.IsRefund) RefreshMethod();
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
                    var next = enteredRubles * 10 + digit;
                    // The refund ceiling is enforced HERE rather than left to the service. The
                    // domain caps the refund too, but a sheet that accepts 500 ₽ against a 220 ₽
                    // payment and then silently books 220 is telling the operator one thing and
                    // doing another; refusing the keystroke makes the sheet's promise true.
                    if (!request.IsRefund || next <= RefundCeilingRubles)
                    {
                        enteredRubles = next;
                    }
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

        // The over-amount line means opposite things in the two directions, which is why it is
        // branched rather than reused. Collecting: the operator tendered MORE than the balance, so
        // the surplus is change they hand back — and since the service clamps the payment down to
        // the balance, this line is the only place they learn what to give out. Refunding: there is
        // no change on a return, and anything above the ceiling is refused at the keypad, so the
        // line stays hidden — "сдача" over money going out is nonsense, and a silently-clamped
        // number would be worse. The ceiling is named in the caption above instead, so the operator
        // knows the limit BEFORE keying rather than after a refused digit.
        var over = enteredRubles - request.AmountDue;
        ChangeLabel.IsVisible = !request.IsRefund && over > 0;
        if (ChangeLabel.IsVisible)
        {
            ChangeLabel.Text = $"Сдача: {TextFormat.Money(over)}";
        }

        // Non-zero in both directions: a confirm button reading "Вернуть оплату" must not be live
        // at 0 ₽, or it books nothing and reports success.

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
