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
/// The payment bottom sheet: what is owed, and one tap to take it — «160,00 ₽ наличными» or
/// «160,00 ₽ картой» — with a keypad behind «Другая сумма» for a partial payment or any other
/// figure. Returns the declared payment, or <c>null</c> when the sheet is dismissed.
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
/// <para>
/// <b>THE COMMON SALE IS ONE TAP, AND THE KEYPAD IS NOT IN THE WAY OF IT.</b> A coffee-shop sale
/// is the same number of taps whatever the tender, so the sheet opens on two full-width buttons
/// that each state the amount they charge and close on the tap. The keypad still exists — partial
/// payment is a real, supported outcome (<c>Order.BalanceKopecks</c>,
/// <c>PaymentState.PartiallyPaid</c>) — but it is behind «Другая сумма», a secondary control
/// clearly below the tender row.
/// </para>
/// <para>
/// <b>THE AMOUNT IS ON THE BUTTON.</b> Baymard's 2024 study captured a tester asking, of a «Next»
/// button, verbatim: «I'm not sure if I click the 'Next' button, will it charge?» — a button
/// naming only a method repeats that ambiguity, because the figure it would charge lives somewhere
/// else on the screen and has to be re-read. <see cref="PaymentSheetResult.Amount"/> already
/// carries a keyed amount, so the contract needed no new field: the same record means «exactly
/// AmountDue, cash» from a tender button and «60 ₽, whatever was typed» from the keypad.
/// </para>
/// <para>
/// <b>CARD NEVER OPENS THE KEYPAD.</b> There is nothing to choose for a card — it is always the
/// balance — so a keypad there would ask the operator to decide something that cannot be decided.
/// </para>
/// <para>
/// <b>IT IS ALSO THE «WHEN DOES THE MONEY MOVE» DECISION, ON THE CART PATH ONLY.</b>
/// «Оплата при выдаче» used to be a second button on the cart beside «Оплатить и создать»; it is now
/// <c>DeferredButton</c> here, under the confirm. It is a decision about when money moves rather than
/// a second kind of checkout, so it belongs where money moves — and on the other two paths that open
/// this sheet (the board's top-up, «Принять оплату» on the details page) the order already exists and
/// there is nothing to defer, which is why it is gated on
/// <see cref="PaymentSheetRequest.AllowDeferredPayment"/> and never on the mode alone. One tap does
/// not change that: it stays visible in both panel states.
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
    /// True once the operator has asked for the keypad on the collect path. The refund path opens
    /// the keypad immediately and never sets it.
    /// </summary>
    private bool keypadOpen;

    /// <summary>
    /// True while the prefilled balance is behaving as if it were SELECTED text: the next digit
    /// replaces it rather than appending to it. The keypad has no cursor and no selection, so this
    /// flag is what «the text is selected» means here, and it is cleared by the first key — which
    /// includes «Сброс» and «Стереть», because those are deliberate acts rather than typing.
    /// </summary>
    private bool replaceEnteredOnNextDigit;

    /// <summary>
    /// True when there is nothing to charge: the sheet then offers no tender button and no keypad.
    /// </summary>
    private bool nothingDue;

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

    /// <summary>
    /// The balance as whole rubles: what one-tap quick pay charges, and what the keypad is
    /// prefilled with under «Другая сумма».
    /// </summary>
    /// <remarks>
    /// Floored, clamped at zero and held under <see cref="MaxEnteredRubles"/>. The floor is the
    /// keypad's own contract — it has no decimal key, so a 220,60 balance can only be entered as
    /// 220 — and the service clamps the recorded payment down to the real balance afterwards, so the
    /// quick-pay figure can never exceed what is owed. Clamping the cap in is what keeps a
    /// pathological request from producing a prefill the keypad would refuse to replace.
    /// </remarks>
    private long BalanceRubles =>
        Math.Clamp((long)Math.Floor(request.AmountDue), 0, MaxEnteredRubles);

    /// <summary>
    /// What to say when the operator tries to confirm nothing. Branched by direction because the
    /// reason is the same but the act is not.
    /// </summary>
    /// <remarks>
    /// No amount is named in the sentence, so no currency symbol is hardcoded in it — a hardcoded
    /// «₽» here would ignore the operator's selected currency, which §5 of .opencode-rules.md
    /// forbids for user-facing money.
    /// </remarks>
    private string ZeroRefusalText => request.IsRefund
        ? "Ноль вернуть нельзя. Возврат на ноль ничего не возвращает, "
          + "но попадает в оплату заказа и сбивает сверку кассы."
        : "Ноль оплатить нельзя. Платёж на ноль ничего не закрывает, "
          + "но попадает в оплату заказа и сбивает сверку кассы.";

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
    /// <para>
    /// A refund gets NO quick pay and NO prefill. It keeps the keypad it always had, starting at 0,
    /// because prefilling it with the ceiling would make «Вернуть оплату» a one-tap return of
    /// everything the till is being asked about — the single most dangerous button this sheet could
    /// grow. The operator chooses how much to give back.
    /// </para>
    /// </remarks>
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

            // The tender buttons state the figure they charge, so it is written here and not bound:
            // PaymentSheetRequest is not this popup's binding context. TextFormat.Money so the
            // amount reads in the operator's currency, and PaymentText.Method so «наличными» /
            // «картой» is spelled the one way the rest of the app spells it.
            var due = TextFormat.Money(request.AmountDue);
            QuickPayCashButton.Text = $"{due} {PaymentText.Method(PaymentMethod.Cash)}";
            QuickPayCardButton.Text = $"{due} {PaymentText.Method(PaymentMethod.Card)}";
            SemanticProperties.SetDescription(QuickPayCashButton, $"Оплатить {due} наличными");
            SemanticProperties.SetDescription(QuickPayCardButton, $"Оплатить {due} картой");
        }

        // Nothing owed: no tender button and no keypad, because there is nothing to charge and a
        // keypad over a zero balance would invite a figure the domain would only clamp away. The
        // label says why rather than leaving the operator to infer it from missing buttons.
        nothingDue = request.AmountDue <= 0;
        NothingDueLabel.Text = request.IsRefund
            ? "Возвращать нечего: в оплате заказа нет денег."
            : "Платить нечего: долга нет.";

        // «Оплата при выдаче», shown only where the caller asked for it. The refund branch above cannot
        // reach it even if it asked: ShowsDeferredPayment is false for a refund whatever the flag says,
        // and deferring a refund would be nonsense anyway.
        //
        // The two are hidden TOGETHER because the rule is between them: showing the rule with no button
        // under it, or a button with nothing above it, each reads as a layout fault.
        //
        // NOT gated on nothingDue. It takes no money, so it is not a tender button, and on a nil
        // cart total it is the only way to complete the checkout at all.
        DeferredButton.IsVisible = DeferredSeparator.IsVisible = request.ShowsDeferredPayment;

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

        RefreshPanels();
        Refresh();
        if (!request.IsRefund) RefreshMethod();
    }

    /// <summary>
    /// One tap on a tender button: record exactly <see cref="PaymentSheetRequest.AmountDue"/> by
    /// that method and close.
    /// </summary>
    /// <remarks>
    /// The amount is <see cref="BalanceRubles"/> and not the entered one, because there is no entered
    /// one on this path — nothing has been typed. The figure is floored, which is the keypad's whole
    /// rubles contract, and the service clamps a payment down to the real balance afterwards, so this
    /// can charge less than the balance but never more.
    /// <para>
    /// <see cref="PaymentSheetResult"/> is returned in exactly the shape the keyed path returns it in.
    /// That is the whole point of the design: the callers cannot tell the two apart, and none of them
    /// needed changing to get one-tap payment.
    /// </para>
    /// </remarks>
    private void OnTenderClicked(object? sender, EventArgs e)
    {
        var method = (sender as Button)?.CommandParameter?.ToString() == "card"
            ? PaymentMethod.Card
            : PaymentMethod.Cash;

        _ = CloseAsync(new PaymentSheetResult(BalanceRubles, method));
    }

    /// <summary>
    /// «Другая сумма»: swap the tender row for the keypad, prefilled with the balance.
    /// </summary>
    /// <remarks>
    /// The prefill is there so the common case — «pay the balance, but let me type it» — needs no
    /// typing at all, and <see cref="replaceEnteredOnNextDigit"/> is set with it so the first digit
    /// REPLACES the balance instead of appending to it. Appending would be the worse bug of the two
    /// and a quiet one: 160 prefilled, then «5» and «0» would read 1 6 0 5 0 and be refused by the
    /// refund-style cap logic only on the collect path's behalf, so the operator would be told
    /// nothing and would conclude the keypad is broken.
    /// <para>
    /// Collect only. A refund never reaches this handler — there is no «Другая сумма» — so the
    /// refund keypad still starts at 0.
    /// </para>
    /// </remarks>
    private void OnOtherAmountClicked(object? sender, EventArgs e)
    {
        keypadOpen = true;
        enteredRubles = BalanceRubles;
        replaceEnteredOnNextDigit = enteredRubles > 0;
        ZeroRefusalLabel.IsVisible = false;

        RefreshPanels();
        Refresh();
    }

    /// <summary>
    /// Swaps between the two payment panels. The single owner of that decision, so the states cannot
    /// disagree with each other.
    /// </summary>
    /// <remarks>
    /// Three panels, three independent flags, and every combination is reachable:
    /// quick pay (collect, balance &gt; 0, keypad closed), the keypad (collect after «Другая сумма»,
    /// or any refund), and the nothing-due label. Nothing is hidden behind a rule without its
    /// button, which is the layout fault the deferred separator comment warns about.
    /// </remarks>
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
                // Not typing — a deliberate act on what is on screen, so the prefill stops being a
                // selection here. Otherwise «Стереть» would clear the balance and the next digit
                // would overwrite the operator's own backspace instead of extending it.
                replaceEnteredOnNextDigit = false;
                break;
            case "clear":
                enteredRubles = 0;
                replaceEnteredOnNextDigit = false;
                break;
            default:
                if (int.TryParse(key, out var digit))
                {
                    // A prefilled balance behaves as if it were selected text: this digit REPLACES it.
                    // The whole-rubles cap cannot apply to that first digit — a single digit is
                    // always inside it — but it does to every digit after it, which is the point of
                    // the cap: it stops a stray keypress printing a 20-digit number.
                    var next = replaceEnteredOnNextDigit ? digit : enteredRubles * 10 + digit;
                    var withinCap = replaceEnteredOnNextDigit || enteredRubles <= MaxEnteredRubles / 10;
                    replaceEnteredOnNextDigit = false;

                    // The refund ceiling is enforced HERE rather than left to the service. The
                    // domain caps the refund too, but a sheet that accepts 500 ₽ against a 220 ₽
                    // payment and then silently books 220 is telling the operator one thing and
                    // doing another; refusing the keystroke makes the sheet's promise true.
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

    /// <summary>
    /// The keyed path's confirm: take what was typed, by the method chosen above it.
    /// </summary>
    /// <remarks>
    /// The entered amount is passed as typed; the service clamps it to the balance. The change
    /// the operator hands back is entered − balance, which is what ChangeLabel showed.
    /// <para>
    /// Zero is REFUSED WITH A REASON rather than a disabled button. «Оплатить ноль» is not a thing:
    /// a zero-value row would still be written to <c>OrderPayment</c>, would leave
    /// <c>PaidKopecks</c> unchanged, and would land in the shift reconciliation as a row that
    /// accounts for nothing. A greyed button says «not now» and leaves the operator to guess;
    /// this says why, in a sentence, next to the control they pressed.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// «Оплата при выдаче»: no money is taken and the order is booked to be paid on collection.
    /// </summary>
    /// <remarks>
    /// The amount and method are written as fillers rather than as what the operator entered, and both
    /// are meaningless on this path — <see cref="PaymentSheetResult.IsDeferred"/> is the whole message,
    /// and the caller branches on it before it reads either. <c>enteredRubles</c> is passed as 0 on
    /// purpose: whatever the operator keyed in before changing their mind is not a payment, and
    /// handing the caller a figure it might mistakenly book is a worse failure than handing it a zero
    /// it cannot.
    /// <para>
    /// Dismissal is the same outcome as tapping outside the sheet, so this is a CloseAsync like any
    /// other — a caller that gets a non-null result has a decision either way and must read the flag.
    /// <b>And one tap does not change this: the button is live on the quick-pay panel as well as on
    /// the keypad panel</b>, so an operator who pays on collection has not lost the exit to the
    /// sheet's new default. Deferring is not a fallback for being unable to charge the balance; it
    /// is the other answer to the same question, and it is gated on the caller, never on the panel.
    /// </para>
    /// </remarks>
    private void OnDeferredClicked(object? sender, EventArgs e) =>
        _ = CloseAsync(new PaymentSheetResult(0, selectedMethod, IsDeferred: true));

    /// <summary>
    /// Redraws the entered amount, the change line and the confirm button. Called after every key.
    /// </summary>
    /// <remarks>
    /// The confirm button is deliberately NOT disabled at 0 any more. See <see cref="OnConfirmClicked"/>:
    /// a refusal the operator can read beats an inert control they have to interpret, and the amount
    /// on the quick-pay buttons has already made the common case a single tap that never touches this
    /// method — so there is no short-circuit being removed here, only an unexplained grey button.
    /// </remarks>
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
