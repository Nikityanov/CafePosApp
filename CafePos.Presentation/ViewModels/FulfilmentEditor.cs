using CafePos.Core.Common;
using CafePos.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CafePos.Presentation.ViewModels;

/// <summary>
/// Three facts about the ORDER rather than about any line: eating in or taking away, the phone
/// kept for a takeaway, and the time it was promised for.
/// </summary>
/// <remarks>
/// <para>
/// The third Collaborator. One dependency — the clock — because the promised time is read from
/// it and nothing else here touches anything.
/// </para>
/// <para>
/// <b>THE SHELL PROXIES ALL OF IT.</b> Every property below is bound by name in MenuPage.xaml,
/// and the binding surface is not a reason to keep the state. The shell re-announces on the
/// editor's <see cref="PropertyChanged"/> rather than mirroring the values.
/// </para>
/// </remarks>
public sealed partial class FulfilmentEditor : ObservableObject
{
    private readonly TimeProvider timeProvider;

    public FulfilmentEditor(TimeProvider timeProvider) => this.timeProvider = timeProvider;
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // PROGRESSIVE DISCLOSURE — WHY THESE THREE CONTROLS ARE BEHIND ONE ROW
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // They used to be permanently expanded above the total, and they measured 153dp of the cart
    // block's 344dp on the emulator (see MenuPage.xaml for the [CartDiag] figures). The cart was
    // taller than the product grid it sits under.
    //
    // NN/g's hotel-reservation case is almost exactly this screen: a single-screen design "worked
    // well when users were trying to decide" but "caused usability problems because it included a
    // segment for users to enter their address and credit card information… it's not needed during
    // the exploratory phase". The exploratory phase here is building the cart, which is what the
    // operator spends the whole of this screen doing; the phone and the fulfilment mode are a
    // decision they make once per order, near the end. The rule the owner confirmed is FREQUENCY OF
    // USE, and that is the rule applied: the first level carries the two decisions that are always
    // made, as a statement of the current state, and the rest is one tap away.
    //
    // "Designs that go beyond 2 disclosure levels typically have low usability" — so this is the
    // limit and not the first of several layers. Nothing here is nested inside anything else; the
    // expanded block's children are all at the same level. And Chimera et al. 1994, over a 1296-item
    // hierarchy, found the "multipane and expand/contract interfaces produced significantly faster
    // times than the stable interface" — which is the direction this moves in, and the reason the
    // collapsed row is a real control rather than a label.
    //
    // WHY A ROW AND NOT A SHEET. MD3 restricts modal bottom sheets to mobile and suggests
    // floating/side sheets on tablets; Apple HIG says "Avoid using a sheet to help people navigate
    // your app's content"; and a sheet over a screen whose whole job is tapping dishes would swallow
    // a tap meant for the menu. So the block expands in place, under the row that opened it.
    //
    // WHY IT IS NOT RESET BY THE CART CHANGING. Nothing in the add/remove/quantity paths touches
    // IsFulfilmentExpanded, deliberately: the operator who opened the block to set a time, then
    // added a dish, must not have it collapse under them. Android does not recreate the activity on
    // rotation either — MainActivity declares ConfigurationChanges for ScreenSize | Orientation |
    // Density and is locked to Portrait — so the transient ViewModel injected into this page outlives
    // the whole session on the tab and the state needs no storage of its own.

    private bool isFulfilmentExpanded;

    /// <summary>
    /// Whether the fulfilment/contact/time block is showing its controls. Collapsed by default.
    /// </summary>
    /// <remarks>
    /// Plain state on the ViewModel, NOT on the page, and that placement is the point: a page-level
    /// flag would be lost the first time Shell rebuilt the page, and a ViewModel flag survives every
    /// round trip the operator can actually make. See the remarks above for the round trip this is
    /// measured against.
    /// </remarks>
    public bool IsFulfilmentExpanded
    {
        get => isFulfilmentExpanded;
        set
        {
            // FulfilmentSummary is raised with it, because the row's TEXT depends on whether the
            // block is open — see that property. It used to depend only on the order type and the
            // time, and the setter raised nothing for it, which is exactly how the two strings drift.
            if (SetProperty(ref isFulfilmentExpanded, value))
            {
                OnPropertyChanged(nameof(FulfilmentToggleHint));
                OnPropertyChanged(nameof(FulfilmentSummary));
            }
        }
    }

    /// <summary>
    /// The collapsed row's label, which is <b>two different strings</b> — and it is one computed
    /// property reading one piece of state, not two strings that have to be kept in step.
    /// </summary>
    /// <remarks>
    /// Collapsed, it STATES the current fulfilment («В зале · готово сейчас»). That is the whole job
    /// of the collapsed row: the controls that could change these facts are hidden, so nothing else on
    /// screen says them, and an operator must be able to see that the order is set up the way they
    /// meant without opening anything.
    /// <para>
    /// Expanded, it says only WHAT IS UNDER IT («Параметры выдачи») and nothing more. Expanded, the
    /// fulfilment control directly beneath it is «Сделать с собой» — a statement of the change, not
    /// of the state — and the row below that reads «Готово сейчас». Repeating «В зале · готово
    /// сейчас» above all three would print each fact twice within three rows, and the row would also
    /// contradict the button under it: a row reading «В зале» over a button reading «Сделать с
    /// собой» asks the reader to reconcile two statements of the same fact.
    /// </para>
    /// <para>
    /// Derived from <see cref="IsFulfilmentExpanded"/>, <see cref="OrderType"/> and
    /// <see cref="RequestedAt"/> in one place — through <see cref="RequestedTimeText"/>, which is the
    /// same string the «Когда» row under it prints. Two independent strings would let the collapsed row
    /// and the controls beneath it disagree, and nothing in the compiler would notice.
    /// </para>
    /// </remarks>
    public string FulfilmentSummary => IsFulfilmentExpanded
        ? FulfilmentRowTitle
        : $"{OrderTypeText} · готово {(requestedAt is null ? "сейчас" : $"к {RequestedTimeText}")}";

    /// <summary>
    /// The neutral heading the row falls back to while the block is open. A constant rather than an
    /// inline literal in the expression above, because <see cref="FulfilmentToggleHint"/> states it
    /// too and two copies of a caption are two things to reword.
    /// </summary>
    public const string FulfilmentRowTitle = "Параметры выдачи";

    /// <summary>
    /// What the collapsed row announces, and what pressing it does. A screen reader hears the two
    /// decisions AND the affordance — the chevron is a shape with no text of its own, so without this
    /// the row would be announced as a static line and nothing would say it opens.
    /// </summary>
    /// <remarks>
    /// Branched on the same <see cref="IsFulfilmentExpanded"/> the row's own caption branches on, so
    /// the announcement and the visible text cannot describe different states.
    /// </remarks>
    public string FulfilmentToggleHint => IsFulfilmentExpanded
        ? $"{FulfilmentRowTitle}. Свернуть параметры выдачи."
        : $"{FulfilmentSummary}. Показать параметры выдачи: способ выдачи, телефон и время.";

    /// <summary>
    /// How the order is fulfilled. <b>It is what decides whether a contact field exists at all</b> —
    /// see <see cref="IsTakeaway"/>.
    /// </summary>
    private OrderType orderType =OrderType.CounterService;

    /// <summary>
    /// Moves the order to <paramref name="value"/> and reports what it cost.
    /// </summary>
    /// <remarks>
    /// <b>A NAMED OPERATION, NOT A PROPERTY SETTER, AND HERE IS WHY.</b> The first version of the
    /// shell's proxy had a setter that wrote the shell's own field while its getter read this class —
    /// two states, and the write invisible to every read. <c>The_cart_starts_as_counter_service_and_the
    /// _toggle_moves_it</c> caught it. Naming the write makes it impossible to write the wrong one:
    /// there is no setter left to reach for.
    /// </remarks>
    /// <returns>What changed and what to say about it; see <see cref="FulfilmentSwitch.Decide"/>.</returns>
    public FulfilmentSwitch Select(OrderType value)
    {
        var change = FulfilmentSwitch.Decide(orderType, value, !string.IsNullOrWhiteSpace(customerPhone));
        if (!change.Changed) return change;

        OrderType = change.OrderType;
        if (change.ClearsPhone) CustomerPhone = null;

        return change;
    }

    /// <summary>The other type, for the one-button toggle.</summary>
    public OrderType Other =>
        orderType == OrderType.CounterService ? OrderType.Takeaway : OrderType.CounterService;

    /// <summary>Expands or collapses the block in place.</summary>
    public void SetExpanded(bool expanded)
    {
        if (isFulfilmentExpanded == expanded) return;
        IsFulfilmentExpanded = expanded;
    }

    /// <summary>Keeps a number for a takeaway order, or drops it.</summary>
    public void SetPhone(string? phone) => CustomerPhone = phone;

    /// <summary>Promises the order for a moment, or leaves it as soon as possible.</summary>
    public void SetPromise(DateTimeOffset? requestedAt) => RequestedAt = requestedAt;

    /// <summary>
    /// Puts all three back to their starting values, for the moment an order is finished or abandoned.
    /// </summary>
    /// <remarks>
    /// <b>THE EXPANDED FLAG IS NOT RESET, DELIBERATELY.</b> It is not part of the order — it is whether
    /// the block is open — and the original code left it open across a checkout too. Two call sites
    /// cleared the other three one assignment at a time, and both had to be kept in step with any fourth
    /// field added later; the mistake is invisible because the screen still looks right.
    /// </remarks>
    public void Reset()
    {
        OrderType = OrderType.CounterService;
        CustomerPhone = null;
        RequestedAt = null;
    }

    /// <summary>Counter service or takeaway.</summary>
    public OrderType OrderType
    {
        get => orderType;
        private set
        {
            if (!SetProperty(ref orderType, value)) return;
            // Three dependents: the phone row's visibility and its empty state, and the checkout
            // intent's first field — announced together because they are one decision, which is also
            // why HasNoCustomerPhone is in the list: it is IsTakeaway AND no number, and a stale copy
            // of it would leave the row showing the wrong one of two labels.
            OnPropertyChanged(nameof(IsCounterService));
            OnPropertyChanged(nameof(IsTakeaway));
            OnPropertyChanged(nameof(HasNoCustomerPhone));
            // …the action button's own caption, which now names the change it makes rather than the
            // state it is in, and therefore has to be re-announced with it.
            OnPropertyChanged(nameof(ToggleOrderTypeText));
            // …and the collapsed row, which states this decision in words.
            OnPropertyChanged(nameof(FulfilmentSummary));
        }
    }

    /// <summary>True for counter service — the default, and the sale that stores the least personal data.</summary>
    public bool IsCounterService => OrderType == OrderType.CounterService;

    /// <summary>
    /// True when the customer takes the order away, and therefore the only case in which a phone is
    /// asked for.
    /// </summary>
    /// <remarks>
    /// 152-ФЗ ст. 6(1)(5) permits a phone to be processed only where it is needed for the performance
    /// of the contract, and ст. 5(7) requires erasing it once that purpose is met. A customer eating
    /// on the premises needs to be found in no way at all, so counter service does not collect one and
    /// <see cref="ICheckoutService"/> would drop it at the service boundary even if it arrived.
    /// EDPB Guidelines 2/2019 п. 25 is the same argument from the other side: where a less intrusive
    /// option exists, the processing is unnecessary in the first place.
    /// </remarks>
    public bool IsTakeaway => OrderType == OrderType.Takeaway;

    /// <summary>
    /// The two state words, kept apart from the button caption on purpose.
    /// </summary>
    /// <remarks>
    /// They were two public properties because the fulfilment control was two segments, each wearing
    /// the word for the state it set. It is one button now (see <see cref="ToggleOrderTypeText"/>), so
    /// the state is stated in exactly one place — the collapsed row, via
    /// <see cref="OrderTypeText"/> — and these are private. Same two strings, same spellings, so the
    /// collapsed row reads «В зале · готово сейчас» exactly as it read before.
    /// </remarks>
    private const string CounterServiceText = "В зале";

    private const string TakeawayText = "С собой";

    /// <summary>
    /// How the order is fulfilled, in the collapsed row's words. The same two strings and the same
    /// <c>OrderTypeText</c> shape <c>OrderDetailsViewModel</c> uses, so the two order screens cannot
    /// describe the same order differently.
    /// </summary>
    public string OrderTypeText => IsTakeaway ? TakeawayText : CounterServiceText;

    /// <summary>
    /// The one fulfilment button's caption: <b>the change it makes</b> — «Сделать с собой» on a
    /// counter-service order, «Сделать в зале» on a takeaway one.
    /// </summary>
    /// <remarks>
    /// Why not two segments. «В зале» / «С собой» restated what the collapsed row already said, and
    /// while the block was open the button would then have sat directly beneath a row saying «В
    /// зале» — the control restating the state its own caption denies. A button that names the
    /// change is unambiguous without a second segment to read, and it is one target instead of two,
    /// which is one fewer thing to mis-tap on a screen whose totals are money.
    /// <para>
    /// The current state is still reachable, at no cost in height: the collapsed row states it (and
    /// collapses back to «Параметры выдачи» only once this button is visible — see
    /// <see cref="FulfilmentSummary"/>), the button's own wording implies it by naming the opposite,
    /// and the phone row appears only for takeaway.
    /// </para>
    /// </remarks>
    public string ToggleOrderTypeText => IsTakeaway
        ? "Сделать в зале"
        : "Сделать с собой";

    /// <summary>
    /// What that button does, for a screen reader: the state, then the change — because the visible
    /// caption is deliberately the second and never the first.
    /// </summary>
    public string ToggleOrderTypeHint =>
        $"Сейчас: {OrderTypeText.ToLowerInvariant()}. {ToggleOrderTypeText}.";

    private string? customerPhone;

    /// <summary>
    /// The contact for a takeaway order, in E.164, or <c>null</c> — which is a normal state and not a
    /// missing value: the customer may decline to give one.
    /// </summary>
    /// <remarks>
    /// Held only while the order is on the cart and written by <see cref="ICheckoutService"/>, which
    /// normalises it and drops it entirely for counter service. A phone in this app is a contact for
    /// one order and nothing else — no SMS, no mailing, and typing a number subscribes the customer to
    /// nothing.
    /// </remarks>
    public string? CustomerPhone
    {
        get => customerPhone;
        private set
        {
            if (!SetProperty(ref customerPhone, value)) return;
            OnPropertyChanged(nameof(PhoneDisplayText));
            OnPropertyChanged(nameof(HasCustomerPhone));
            OnPropertyChanged(nameof(HasNoCustomerPhone));
        }
    }

    /// <summary>
    /// The number as the CUSTOMER may see it, masked: <c>+7 ••• ••• •• 42</c>.
    /// </summary>
    /// <remarks>
    /// <b>The cart is the customer-facing surface</b>, so the mask goes here and nowhere else on this
    /// page. PCI DSS 3.4.1 asks for at least six leading and four trailing digits of a PAN to be
    /// masked; it is written about a card number, so its figure is a floor rather than a shape, and
    /// <see cref="PhoneNumber.Mask"/> hides more than the floor. Eight of eleven digits stay covered on
    /// a Russian number, with enough left for the customer to recognise their own number as they read
    /// it back to the cashier — which is the whole point of the confirm step in
    /// <see cref="EditPhoneAsync"/>. The unmasked number belongs only on the staff-facing
    /// <c>OrderDetailsPage</c>.
    /// </remarks>
    public string PhoneDisplayText => customerPhone is null ? string.Empty : PhoneNumber.Mask(customerPhone);

    /// <summary>Whether a masked number is on screen to show.</summary>
    public bool HasCustomerPhone => !string.IsNullOrWhiteSpace(PhoneDisplayText);

    /// <summary>
    /// The empty state of the contact row for a takeaway order: asked, and not yet answered. A separate
    /// flag rather than a computed one so the row has a label in both states — a row that is simply
    /// blank reads as a control that has not loaded yet.
    /// </summary>
    public bool HasNoCustomerPhone => IsTakeaway && !HasCustomerPhone;

    // The two per-face style properties this ViewModel carried for the fulfilment toggle
    // (CounterServiceButtonStyle / TakeawayButtonStyle, off "PaymentMethodActive" /
    // "PaymentMethodButton") are GONE with the second segment. A one-button toggle has no second
    // face to tint, and a button that wears a "selected" style would be claiming a selection while
    // actually stating an action. The AppThemeBinding-inside-a-Setter trap they were written around
    // is still live elsewhere and still reasoned out at MessageColor.

    private DateTimeOffset? requestedAt;

    /// <summary>
    /// The time the customer asked for, or <c>null</c> for as soon as possible.
    /// </summary>
    /// <remarks>
    /// An absent time IS "as soon as possible" and the promise is then
    /// <c>CreatedAt + <see cref="Order.LeadTimeMinutes"/></c>. There is deliberately no ASAP flag
    /// beside this: a flag would store the same absence twice, and the two copies could disagree — one
    /// column saying "ASAP" while the other holds a time.
    /// </remarks>
    public DateTimeOffset? RequestedAt
    {
        get => requestedAt;
        private set
        {
            if (!SetProperty(ref requestedAt, value)) return;
            OnPropertyChanged(nameof(RequestedTimeText));
            OnPropertyChanged(nameof(IsRequestedTimeLate));
            OnPropertyChanged(nameof(PromiseText));
            // The collapsed row states the time in words too.
            OnPropertyChanged(nameof(FulfilmentSummary));
        }
    }

    /// <summary>
    /// Whether the named time is already behind us — an order promised for a moment that has gone,
    /// which the board shows as overdue.
    /// </summary>
    /// <remarks>
    /// MARKED, NOT REFUSED, and the sheet is where the decision is made. A customer who said «в 14:20»
    /// and is still waiting at 15:00 is the ordinary reason this control exists, and a sheet that
    /// refused the time would leave the cashier unable to write down what is actually true. The order
    /// books with a <c>PromisedAt</c> in the past; this is what says so at the till.
    /// <para>
    /// A computed reading of the wall clock rather than a stored flag, so it cannot disagree with the
    /// order — but it is therefore only as fresh as the last time something announced it. That is why
    /// <see cref="RequestedTimeText"/> and this are both re-raised by <see cref="LoadAsync"/>, which
    /// Shell drives on every return to the tab, and by every change to <see cref="RequestedAt"/>.
    /// </para>
    /// </remarks>
    public bool IsRequestedTimeLate => OrderPromise.IsLate(requestedAt, timeProvider.GetLocalNow());

    /// <summary>What the "когда" control currently reads: «сейчас», or the chosen clock time.</summary>
    public string RequestedTimeText => requestedAt is null
        ? "сейчас"
        : ClockTime.Format(requestedAt.Value, timeProvider.LocalTimeZone);

    /// <summary>
    /// The promise, as the CUSTOMER reads it. A range when the time is the till's to estimate, a clock
    /// time only when the customer named one.
    /// </summary>
    /// <remarks>
    /// <b>WHY A RANGE AND NOT A POINT.</b> Management Science 71(9) 2025, eight preregistered
    /// experiments, N=5323: a range is rated better than a point estimate even when the point estimate
    /// equals the range's upper bound — and an extremely wide range is rated WORSE than a point. So the
    /// figure below is widened, not shifted, and only modestly.
    /// <para>
    /// A requested time prints as a clock time on purpose, and it is not the same claim: it is what
    /// the customer asked for, repeated back so they can correct it — not an estimate the kitchen
    /// invented. Quoting a range for an agreed appointment would only obscure what was agreed.
    /// </para>
    /// </remarks>
    public string PromiseText => requestedAt is { } at
        ? $"Будет готово к {ClockTime.Format(at)}"
        : $"Будет готово через {PromiseFromMinutes}–{PromiseToMinutes} мин";

    /// <summary>
    /// How far the customer-facing range is widened around the lead time, in minutes.
    /// </summary>
    /// <remarks>
    /// Two minutes each way is the whole width of the claim: enough that the range is a range (4
    /// minutes of spread at the default 10-minute lead time, so it reads as a genuine estimate
    /// rather than a rounded single figure), and nowhere near the width at which the study's finding
    /// reverses. A range spanning half the lead time would be the "extremely wide" case that scores
    /// below a point estimate, and a zero-width range would be the point estimate the study shows
    /// losing.
    /// <para>
    /// The lead time itself is read from <see cref="Order.LeadTimeMinutes"/>, which
    /// <c>AddCafePosCore</c> assigns once at startup from <c>DatabaseOptions</c> — so the figure
    /// quoted to a customer is the same number the promise is computed from and there is no second
    /// constant here to drift.
    /// </para>
    /// </remarks>
    private const int PromiseSlackMinutes = 2;

    /// <summary>Lower bound of the quoted range. Floored at one minute, never zero.</summary>
    private static int PromiseFromMinutes => Math.Max(1, Order.LeadTimeMinutes - PromiseSlackMinutes);

    /// <summary>Upper bound: the lead time plus the slack, so the promise itself is inside the range.</summary>
    private static int PromiseToMinutes => Order.LeadTimeMinutes + PromiseSlackMinutes;
}
