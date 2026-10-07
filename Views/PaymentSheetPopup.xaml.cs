using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

// The payment bottom sheet: what is owed, and one tap to take it — «160,00 ₽ наличными» or «160,00 ₽ картой» — with a keypad behind «Другая сумма» for a partial payment or any other figure. Returns the declared payment, or null when the sheet is dismissed.
// Почему так — `docs/decisions/payment-sheet.md`

[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class PaymentSheetPopup : Popup<PaymentSheetResult?>
{
    /// <summary>Whole rubles. A coffee-shop check never approaches this; it only stops a stray keypress from printing a 20-digit number.</summary>
    private const long MaxEnteredRubles = 999_999;

    private readonly PaymentSheetRequest request;

    private long enteredRubles;
    private PaymentMethod selectedMethod = PaymentMethod.Cash;

    /// <summary>True once the operator has asked for the keypad on the collect path. The refund path opens the keypad immediately and never sets it.</summary>

    private bool keypadOpen;

    /// <summary>True while the prefilled balance is behaving as if it were SELECTED text: the next digit replaces it rather than appending to it. The keypad has no cursor and no selection, so this flag is what «the text is selected» means here, and it is cleared by the first key — which includes «Сброс» and «Стереть», because those are deliberate acts rather than typing.</summary>

    private bool replaceEnteredOnNextDigit;

    /// <summary>
    /// True when there is nothing to charge: the sheet then offers no tender button and no keypad.
    /// </summary>
    private bool nothingDue;

    /// <summary>The most that may be keyed in on a refund, in whole rubles, floored.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private long RefundCeilingRubles => (long)Math.Floor(request.AmountDue);

    /// <summary>The balance as whole rubles: what one-tap quick pay charges, and what the keypad is prefilled with under «Другая сумма».</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private long BalanceRubles =>
        Math.Clamp((long)Math.Floor(request.AmountDue), 0, MaxEnteredRubles);

    /// <summary>What to say when the operator tries to confirm nothing. Branched by direction because the reason is the same but the act is not.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private string ZeroRefusalText => request.IsRefund
        ? "Ноль вернуть нельзя. Возврат на ноль ничего не возвращает, "
          + "но попадает в оплату заказа и сбивает сверку кассы."
        : "Ноль оплатить нельзя. Платёж на ноль ничего не закрывает, "
          + "но попадает в оплату заказа и сбивает сверку кассы.";

    public PaymentSheetPopup(PaymentSheetRequest request)
    {
        InitializeComponent();
        this.request = request;

        // Full-width bottom sheet. The toolkit converts HorizontalOptions.Fill back to Center, so the width is set explicitly instead — the same fix as CatalogActionSheetPopup. The window width is used (not the physical display width) so the sheet fits in split-screen windows.

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

    /// <summary>Rewrites the sheet for the direction the money is going, then draws the first frame.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void ApplyMode(PaymentSheetRequest request)
    {
        if (request.IsRefund)
        {
            AmountDueLabel.Text = $"Вернуть можно: {TextFormat.Money(request.AmountDue)}";
            RefundNoteLabel.Text = "Деньги уходят из кассы покупателю. Способ возврата не выбирается: "
                                 + "он совпадает с тем, как заказ принимали.";
            RefundNoteLabel.IsVisible = true;
            ConfirmButton.Text = "Вернуть оплату";
            SemanticProperties.SetDescription(ConfirmButton, "Вернуть оплату покупателю");
            MethodGrid.IsVisible = false;
        }
        else
        {
            AmountDueLabel.Text = $"К оплате: {TextFormat.Money(request.AmountDue)}";

            // The tender buttons state the figure they charge, so it is written here and not bound: PaymentSheetRequest is not this popup's binding context. TextFormat.Money so the amount reads in the operator's currency, and PaymentText.Method so «наличными» / «картой» is spelled the one way the rest of the app spells it.

            var due = TextFormat.Money(request.AmountDue);
            QuickPayCashButton.Text = $"{due} {PaymentText.Method(PaymentMethod.Cash)}";
            QuickPayCardButton.Text = $"{due} {PaymentText.Method(PaymentMethod.Card)}";
            SemanticProperties.SetDescription(QuickPayCashButton, $"Оплатить {due} наличными");
            SemanticProperties.SetDescription(QuickPayCardButton, $"Оплатить {due} картой");
        }

        // Nothing owed: no tender button and no keypad, because there is nothing to charge and a keypad over a zero balance would invite a figure the domain would only clamp away. The label says why rather than leaving the operator to infer it from missing buttons.

        nothingDue = request.AmountDue <= 0;
        NothingDueLabel.Text = request.IsRefund
            ? "Возвращать нечего: в оплате заказа нет денег."
            : "Платить нечего: долга нет.";

        // «Оплата при выдаче», shown only where the caller asked for it. The refund branch above cannot reach it even if it asked: ShowsDeferredPayment is false for a refund whatever the flag says, and deferring a refund would be nonsense anyway. The two are hidden TOGETHER because the rule is between them: showing the rule with no button under it, or a button with nothing above it, each reads as a layout fault. NOT gated on nothingDue. It takes no money, so it is not a tender button, and on a nil cart total it is the only way to complete the checkout at all.

        DeferredButton.IsVisible = DeferredSeparator.IsVisible = request.ShowsDeferredPayment;

        // "Already settled in this direction": money collected on a top-up, money returned on a partial refund. Without this line a second refund reads as a second refund of the FULL original amount rather than of what is left, which is how an operator over-returns.

        AlreadyPaidLabel.IsVisible = request.AlreadyPaid > 0;
        if (request.AlreadyPaid > 0)
        {
            AlreadyPaidLabel.Text = request.IsRefund
                ? $"Уже возвращено: {TextFormat.Money(request.AlreadyPaid)}"
                : $"Уже оплачено: {TextFormat.Money(request.AlreadyPaid)} · доплата";
        }

        RefreshPanels();
        Refresh();
        if (!request.IsRefund) RefreshMethod();
    }

    /// <summary>One tap on a tender button: record exactly by that method and close.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void OnTenderClicked(object? sender, EventArgs e)
    {
        var method = (sender as Button)?.CommandParameter?.ToString() == "card"
            ? PaymentMethod.Card
            : PaymentMethod.Cash;

        _ = CloseAsync(new PaymentSheetResult(BalanceRubles, method));
    }

    /// <summary>«Другая сумма»: swap the tender row for the keypad, prefilled with the balance.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void OnOtherAmountClicked(object? sender, EventArgs e)
    {
        keypadOpen = true;
        enteredRubles = BalanceRubles;
        replaceEnteredOnNextDigit = enteredRubles > 0;
        ZeroRefusalLabel.IsVisible = false;

        RefreshPanels();
        Refresh();
    }

    /// <summary>Swaps between the two payment panels. The single owner of that decision, so the states cannot disagree with each other.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void RefreshPanels()
    {
        QuickPayPanel.IsVisible = !nothingDue && !request.IsRefund && !keypadOpen;
        KeypadPanel.IsVisible = !nothingDue && (request.IsRefund || keypadOpen);
        NothingDueLabel.IsVisible = nothingDue;
    }

    private void OnKeyClicked(object? sender, EventArgs e)
    {
        var key = (sender as Button)?.CommandParameter?.ToString();
        switch (key)
        {
            case "backspace":
                // Integer division: drop the last digit.
                enteredRubles = enteredRubles / 10;
                // Not typing — a deliberate act on what is on screen, so the prefill stops being a selection here. Otherwise «Стереть» would clear the balance and the next digit would overwrite the operator's own backspace instead of extending it.

                replaceEnteredOnNextDigit = false;
                break;
            case "clear":
                enteredRubles = 0;
                replaceEnteredOnNextDigit = false;
                break;
            default:
                if (int.TryParse(key, out var digit))
                {
                    // A prefilled balance behaves as if it were selected text: this digit REPLACES it. The whole-rubles cap cannot apply to that first digit — a single digit is always inside it — but it does to every digit after it, which is the point of the cap: it stops a stray keypress printing a 20-digit number.

                    var next = replaceEnteredOnNextDigit ? digit : enteredRubles * 10 + digit;
                    var withinCap = replaceEnteredOnNextDigit || enteredRubles <= MaxEnteredRubles / 10;
                    replaceEnteredOnNextDigit = false;

                    // The refund ceiling is enforced HERE rather than left to the service. The domain caps
                    // the refund too, but a sheet that accepts 500 ₽ against a 220 ₽ payment and then
                    // silently books 220 is telling the operator one thing and doing another; refusing
                    // the keystroke makes the sheet's promise true.

                    if (withinCap && (!request.IsRefund || next <= RefundCeilingRubles))
                    {
                        enteredRubles = next;
                    }
                }
                break;
        }

        // The amount changed, so any standing refusal is stale.
        ZeroRefusalLabel.IsVisible = false;
        Refresh();
    }

    private void OnMethodClicked(object? sender, EventArgs e)
    {
        selectedMethod = (sender as Button)?.CommandParameter?.ToString() == "card"
            ? PaymentMethod.Card
            : PaymentMethod.Cash;
        RefreshMethod();
    }

    /// <summary>The keyed path's confirm: take what was typed, by the method chosen above it.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void OnConfirmClicked(object? sender, EventArgs e)
    {
        if (enteredRubles <= 0)
        {
            ZeroRefusalLabel.Text = ZeroRefusalText;
            ZeroRefusalLabel.IsVisible = true;
            return;
        }

        _ = CloseAsync(new PaymentSheetResult(enteredRubles, selectedMethod));
    }

    /// <summary>«Оплата при выдаче»: no money is taken and the order is booked to be paid on collection.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void OnDeferredClicked(object? sender, EventArgs e) =>
        _ = CloseAsync(new PaymentSheetResult(0, selectedMethod, IsDeferred: true));

    /// <summary>Redraws the entered amount, the change line and the confirm button. Called after every key.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

    private void Refresh()
    {
        EnteredLabel.Text = TextFormat.Money(enteredRubles);

        // The over-amount line means opposite things in the two directions, which is why it is branched rather than reused. Collecting: the operator tendered MORE than the balance, so the surplus is change they hand back — and since the service clamps the payment down to the balance, this line is the only place they learn what to give out. Refunding: there is no change on a return, and anything above the ceiling is refused at the keypad, so the line stays hidden — "сдача" over money going out is nonsense, and a silently-clamped number would be worse. The ceiling is named in the caption above instead, so the operator knows the limit BEFORE keying rather than after a refused digit.

        var over = enteredRubles - request.AmountDue;
        ChangeLabel.IsVisible = !request.IsRefund && over > 0;
        if (ChangeLabel.IsVisible)
        {
            ChangeLabel.Text = $"Сдача: {TextFormat.Money(over)}";
        }
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

    /// <summary>Matches the sheet to the window it is shown over, and lifts its content above the system navigation bar.</summary>
    /// <remarks>Почему так — `docs/decisions/payment-sheet.md`</remarks>

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

    /// <summary>The height of the system navigation bar in device-independent pixels, or 0 when there is none. Android-only: the other platforms either have no bottom bar or inset their content themselves.</summary>

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
