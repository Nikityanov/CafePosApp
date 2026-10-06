using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Errors;
using CafePos.Core.Models;
using CafePos.Core.Services;
// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out of
// this change's write scope; it belongs beside ResourceStyles.TryGetColor. See its own remarks.
using CafePosApp.Converters;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

/// <summary>
/// The three sections the board is divided into. NOT the order's status, and not its age.
/// </summary>
/// <remarks>
/// <b>WHY THREE SECTIONS AND NOT "PREPARING / READY".</b> The promise gates when an order is worked,
/// not which of two statuses it holds. Toast keeps a future order in "Pending Orders → Future Checks"
/// where it "appears as a preview ticket until the fire time is met", and DoorDash moves scheduled
/// orders into the active tab by pickup time — Square does the same. An order for 18:00 placed at
/// 17:00 is not "готовится" and not "готов": it is not yet anybody's work, and putting it among the
/// working orders tells the kitchen to start now and then shows it as overdue before a minute has
/// passed.
/// <para>
/// The three are checked in one order and are mutually exclusive: a future time wins over being late
/// (an order promised for later cannot be late), and lateness wins over the plain working section.
/// Together they cover every active order, which is what lets the board sort by section as its PRIMARY
/// key without leaving rows ungrouped.
/// </para>
/// </remarks>
public enum QueueSection
{
    /// <summary>Being made and the promised time has already passed. Look here first.</summary>
    Urgent = 0,

    /// <summary>Being made, on time, and not promised for later.</summary>
    Working = 1,

    /// <summary>The customer named a time that has not arrived yet. A preview, not work.</summary>
    Scheduled = 2
}

/// <summary>
/// The three tabs of the orders board, in the order they appear.
/// </summary>
/// <remarks>
/// An ENUM rather than a 0/1/2 index, and the crash log is the reason. With
/// <c>RelayCommand&lt;int&gt;</c> and <c>CommandParameter="0"</c> in XAML, MAUI hands the parameter
/// over as the STRING "0" and CommunityToolkit's RelayCommand throws
/// <c>System.ArgumentException: Parameter "parameter" (object) cannot be of type System.String</c> the
/// moment the chip is tapped — the board died on its first tab switch. An XAML attribute literal is
/// always a string, so an int-typed command parameter cannot be written that way at all: a command
/// parameter has to be bound, and the only markup that yields a real typed value is
/// <c>{x:Static}</c>.
/// <para>
/// AT NAMESPACE LEVEL, not nested inside <see cref="OrdersViewModel"/>. Nested, the type is
/// <c>CafePosApp.ViewModels.OrdersViewModel+OrderTab</c> and the XAML compiler cannot name a nested
/// type through a namespace prefix — it reports <c>XamlParseException: Type OrderTab not found in
/// xmlns clr-namespace:CafePosApp.ViewModels</c> and the page dies at parse time, before a single
/// control is drawn. Both crashes came from this single declaration; the second is the same mistake in
/// a different shape.
/// </para>
/// </remarks>
public enum OrderTab
{
    Preparing,
    Ready,
    Scheduled
}

public partial class OrderRowViewModel : ObservableObject
{
    // Material icon geometry for the three payment states, on the native 24x24 viewBox. Parsed
    // once with the same converter XAML uses for Path="M …" (Geometry has no Parse method).
    private static readonly Geometry PaidGlyph = ParseGeometry("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z");
    private static readonly Geometry UnpaidGlyph = ParseGeometry("M12.5 6.9c1.78 0 2.44.85 2.5 2.1h2.21c-.07-1.72-1.12-3.3-3.21-3.81V3h-3v2.16c-.53.12-1.03.3-1.48.54l1.47 1.47c.41-.17.91-.27 1.51-.27zM5.33 4.06L4.06 5.33 7.5 8.77c0 2.08 1.56 3.21 3.91 3.91l3.51 3.51c-.34.48-1.05.91-2.42.91-2.06 0-2.87-.92-2.98-2.1h-2.2c.12 2.19 1.76 3.42 3.68 3.83V21h3v-2.15c.96-.18 1.82-.55 2.45-1.12l2.22 2.22 1.27-1.27L5.33 4.06z");
    private static readonly Geometry PartialGlyph = ParseGeometry("M11 2v20c-5.07-.5-9-4.79-9-10s3.93-9.5 9-10zm2.03 0v8.99H22c-.47-4.74-4.24-8.52-8.97-8.99zm0 11.01V22c4.74-.47 8.5-4.25 8.97-8.99h-8.97z");

    public OrderRowViewModel(Order model, AppSettings settings, DateTimeOffset now)
    {
        Model = model;
        OrderTitle = $"{settings.OrderPrefix} #{model.OrderNumber}";
        this.now = now;
    }

    /// <summary>
    /// The moment this row was judged at, passed in rather than read from a clock.
    /// </summary>
    /// <remarks>
    /// Every derived value on this row — which section it is in, whether it is late, by how much —
    /// is measured against ONE instant, taken once by the loader. A row that read the clock itself
    /// would compute its section at a slightly different moment from the row above it, and two orders
    /// promised for the same minute could land on opposite sides of a boundary; worse, the section a
    /// row belongs to would change under the operator's finger with no reload at all.
    /// </remarks>
    private readonly DateTimeOffset now;

    private static Geometry ParseGeometry(string path) =>
        (Geometry)new PathGeometryConverter().ConvertFromString(path)!;

    public Order Model { get; }
    public string OrderTitle { get; }

    public string StatusText => Model.Status switch
    {
        OrderStatus.InProgress => "Готовится",
        OrderStatus.Ready => "Готов",
        OrderStatus.Cancelled => "Отменен",
        _ => "Закрыт"
    };

    /// <summary>
    /// The cancellation reason for a voided order, or empty.
    /// </summary>
    /// <remarks>
    /// A separate property rather than appended to <see cref="StatusText"/>, because the shift
    /// report's history row shows the composed reason underneath the status while the orders board
    /// does not need it at all: the board never lists a cancelled order, since
    /// <c>GetActiveOrdersAsync</c> excludes both terminal statuses.
    /// <para>
    /// Worth having on the history row because the domain composes the whole story into this one
    /// string — how much was refunded, whether stock came back, and which ingredients could not be
    /// reversed. A voided sale with only "Отменен" on it tells a manager the order is gone and
    /// nothing about where the money went.
    /// </para>
    /// </remarks>
    public string CancellationDetail =>
        Model.Status == OrderStatus.Cancelled && !string.IsNullOrWhiteSpace(Model.CancellationReason)
            ? Model.CancellationReason
            : string.Empty;

    /// <summary>
    /// Whether this card became ready and nobody has opened it since — the unread dot.
    /// </summary>
    /// <remarks>
    /// The owner asked for "счётчик + точка непрочитанного" and picked the Toast pattern: a marker that
    /// WAITS to be acknowledged rather than one that fades. A decaying highlight answers "something
    /// changed a moment ago"; this answers "is there something you have not seen yet", which is the
    /// question after a walk to the counter or a reboot.
    /// <para>
    /// The judgement is <see cref="Order.IsUnseenReady"/> against the loader's <c>now</c> — the same
    /// instant every other figure on this card is measured at, so a row cannot show a dot for an order
    /// that went ready between the read and the draw. It is a pure function of two stored moments, not a
    /// maintained flag: the board rebuilds its rows on every auto-refresh tick, and a flag carried across
    /// those rebuilds would eventually disagree with <c>ReadyAt</c>.
    /// </para>
    /// <para>
    /// In practice only the «Ждут выдачи» tab shows it — a dot is a statement about a finished order
    /// nobody has collected — but it is computed from the model rather than from the tab, so a ready
    /// order that is also scheduled still says so wherever it appears.
    /// </para>
    /// </remarks>
    public bool IsUnseenReady => Model.IsUnseenReady(now);

    /// <summary>What a screen reader is told about the dot, since a dot alone carries no text.</summary>
    public string UnseenReadyHint => "Готов и ещё не просмотрен";

    /// <summary>
    /// Which section this order is in at the instant the board was loaded.
    /// </summary>
    /// <remarks>
    /// Three questions, asked in this order and never merged: a time in the future means the order is
    /// a preview, a promise already past means it is late, and everything else is ordinary work. The
    /// checks read <see cref="Order.IsScheduledAt"/> and <see cref="Order.IsOverdueAt"/>, which is
    /// where "in the future" and "already past" are defined once for the whole app.
    /// </remarks>
    public QueueSection QueueSection => IsScheduled
        ? QueueSection.Scheduled
        : IsOverdue ? QueueSection.Urgent : QueueSection.Working;

    /// <summary>
    /// Section this order belongs to in the single-list layout. The orders board is one
    /// scrolling list, not two columns, because a kanban on a 411dp phone gave each card
    /// ~190dp and clipped "Подробнее" to "Подробн". Grouping keeps the visual split the
    /// board had while giving every card the full width.
    /// </summary>
    /// <remarks>
    /// A string, not the enum, because the group header template binds to this exact value — the
    /// header is the property, so it has to read as Russian prose. The three names are the plan's own:
    /// «Срочные», «В работе», «По времени».
    /// <para>
    /// The two sections this replaces were the order's STATUS, which is a different question: a ready
    /// order waiting for pickup is still work, and it is still late if its promise has passed. The
    /// status is still on the card — see <see cref="StatusText"/> — it just no longer decides where the
    /// card sits.
    /// </para>
    /// </remarks>
    public string QueueSectionName => QueueSection switch
    {
        QueueSection.Urgent => "Срочные",
        QueueSection.Scheduled => "По времени",
        _ => "В работе"
    };

    /// <summary>Sort key of the section, so «Срочные» is always above «В работе».</summary>
    public int QueueSectionOrder => (int)QueueSection;

    /// <summary>
    /// True on the first card of each section, which is where the section name is drawn.
    /// </summary>
    /// <remarks>
    /// Set once the rows are sorted preparing-first, not from the constructor — a row cannot
    /// know whether an earlier row in the same group exists. The name itself is text in a
    /// heading, so the two sections are told apart by their wording and never by colour alone.
    /// <para>
    /// This raises change notification, which a plain auto-property does not. Switching the
    /// section filter rewrites the flag on rows that are already in the list and leaves the
    /// collection itself structurally unchanged, so CollectionView recycles the cells instead of
    /// re-inflating the templates. Without the notification the heading stayed on screen after
    /// its section had been filtered to a single chip that already names it.
    /// </para>
    /// </remarks>
    public bool ShowGroupHeader
    {
        get => showGroupHeader;
        internal set => SetProperty(ref showGroupHeader, value);
    }

    private bool showGroupHeader;

    // ── Promise, lateness and contact ─────────────────────────────────────────────────────────────
    // THE SORT KEY AND THE LATE MARKER ARE TWO SEPARATE MEASUREMENTS, AND THEY ARE NOT MERGED.
    //
    // Where a card SITS answers "what should be worked on, in what order" — measured by PromisedAt,
    // then CreatedAt. The warning marker answers "has this one passed the moment it was promised" —
    // measured by now against PromisedAt alone, with no reference to where the card is. In a real KDS
    // those are different timers for different reasons: the warning colour on a kitchen screen
    // measures how long an ITEM has been cooking, which is not how late the order's promise is. Folding
    // one into the other would mean a late order could be reordered into a calm stretch of the queue
    // and lose the fact that it is late — which is exactly the failure this layout exists to prevent.
    //
    // So the marker RIDES ALONG on the card and never moves it. The «Срочные» section is where late
    // orders live, and that is a statement about where they are — not a promotion rule applied to the
    // other two sections.

    /// <summary>
    /// Whether the customer asked for a time that has not arrived yet — the order is a preview ticket,
    /// not work.
    /// </summary>
    public bool IsScheduled => Model.IsScheduledAt(now);

    /// <summary>
    /// Whether the promised time has passed while the order is still on the board.
    /// </summary>
    /// <remarks>
    /// A DISPLAY STATE AND NOT A FAILURE: no system found rejects an order whose promised time has
    /// simply elapsed in the queue — the kitchen is behind, which is a fact about the kitchen and not
    /// something a refusal would fix. The board only lists active orders, so "still on the board" is the
    /// whole of "still being made" here, and a ready order waiting for pickup that has run past its
    /// promise is exactly as late as one still in the pan.
    /// </remarks>
    public bool IsOverdue => Model.IsOverdueAt(now);

    /// <summary>Whole minutes past the promise, and never zero on a row that is actually late.</summary>
    public int OverdueMinutes => Math.Max(1, (int)Math.Round((now - Model.PromisedAt).TotalMinutes));

    /// <summary>
    /// The badge. One word, so it fits the tag — the figure is in <see cref="OverdueHint"/>, which is
    /// what a screen reader is handed.
    /// </summary>
    public string OverdueText => "Просрочен";

    /// <summary>The badge's accessible description, carrying the figure the tag leaves out.</summary>
    public string OverdueHint =>
        $"Обещанное время прошло {TextFormat.Plural(OverdueMinutes, "минуту", "минуты", "минут")} назад";

    /// <summary>
    /// The lateness tint: the badge's text.
    /// </summary>
    /// <remarks>
    /// <see cref="StatusColor"/> keeps the card's border stroke, and it must: a card has to be able to
    /// say both "this is cooking" and "this is late", which are separate facts, and recolouring the
    /// border would make a late order look like it had a different STATUS. So the lateness marker is a
    /// badge, and colour is its third signal rather than its first —
    /// <see cref="OverdueText"/> names the state and <see cref="OverdueHint"/> says it in a sentence.
    /// </remarks>
    public Color OverdueColor => ThemeColors.Resolve("Danger", "DangerDark");

    /// <summary>
    /// When this order is due. The EXACT promised time, which a staff-facing screen may show: the range
    /// quoted to the customer on the cart is an estimate for a time the till picks, and this is the
    /// figure the order was actually promised and is judged against.
    /// </summary>
    public string PromisedAtText => Model.PromisedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>
    /// The promise, worded for the kind of promise it is: a time the customer named, or the lead-time
    /// one the till gave.
    /// </summary>
    public string PromiseText => IsScheduled ? $"к {PromisedAtText}" : $"обещано к {PromisedAtText}";

    /// <summary>How the order is fulfilled, in the board's own words.</summary>
    public string OrderTypeText => Model.OrderType == OrderType.Takeaway ? "С собой" : "В зале";

    /// <summary>
    /// The contact, MASKED, or empty.
    /// </summary>
    /// <remarks>
    /// Masked here for the same reason it is masked on the cart: an orders board stands in the room and
    /// is readable by whoever is standing in it, so it is not a staff-only surface in the way
    /// <c>OrderDetailsPage</c> is — a screen somebody opens deliberately, one order at a time. The
    /// unmasked number lives only there.
    /// </remarks>
    public string PhoneText =>
        Model.CustomerPhone is null ? string.Empty : PhoneNumber.Mask(Model.CustomerPhone);

    public string StatusHint => Model.Status switch
    {
        OrderStatus.Ready => "Ждет выдачи",
        OrderStatus.Cancelled => "Заказ отменен",
        OrderStatus.InProgress => "Заказ готовится",
        _ => "Заказ закрыт"
    };

    /// <summary>
    /// The status tint: a card border, the status label and — on the shift report — the history
    /// row's status line.
    /// </summary>
    /// <remarks>
    /// Was four <c>Microsoft.Maui.Graphics.Colors.*</c> literals, which is a colour that can never
    /// follow the theme: <c>AppThemeBinding</c> governs XAML-supplied values only, so a finished
    /// <see cref="Color"/> from a ViewModel stays whatever it was built as. On a
    /// <c>SurfaceDark #1E1E1E</c> card that left <c>SteelBlue #4682B4</c> (closed) at 4.06:1,
    /// <c>Green #008000</c> (ready) at 3.25:1 and <c>Red #FF0000</c> (unpaid) at 4.17:1 — all
    /// under the 4.5:1 a 13pt label owes, and all of them lighter on the SurfaceVariantDark fill
    /// they also appear on. <c>ThemeColors</c> reads the palette and picks the light or dark key
    /// for the live theme; the new dark tones measure 8.28:1, 8.36:1 and 9.63:1 there.
    /// <para>
    /// Two mappings are worth naming, because they are choices and not transcriptions.
    /// <c>Colors.Gray</c> (#808080, 3.95:1 on white) became <c>Gray600</c>/<c>Gray400</c> — the
    /// app's own secondary-text pairing from <c>SecondaryLabel</c> and <c>CountLabel</c>, which
    /// is both more consistent and 4.61:1 in light instead of 3.95:1. And the closed order's
    /// SteelBlue became <c>Info</c>/<c>InfoDark</c>: blue was already carrying "finished, nothing
    /// outstanding", and it is the one semantic the four states could use without colliding with
    /// the green/orange/grey of the other three.
    /// <para>
    /// In-progress takes <c>WarningText</c>/<c>WarningDark</c> rather than
    /// <c>Warning</c>/<c>WarningDark</c>, for the same reason the payment line does: the
    /// palette's only warm tone reached 3.11:1 on the light card, so the light branch of a
    /// warning-coloured <em>label</em> needs a token dark enough to be text. 5.18:1 against
    /// #FFFFFF, 4.96:1 against #FAFAFA, dark branch unchanged at 9.63:1.
    /// </para>
    /// </para>
    /// <para>
    /// Colour is never the only cue here and never was: <see cref="StatusText"/> names the state
    /// and <see cref="StatusHint"/> is the screen-reader description, so re-tuning the tints
    /// changes nothing about what the state is.
    /// </para>
    /// </remarks>
    public Color StatusColor => Model.Status switch
    {
        OrderStatus.Ready => ThemeColors.Resolve("Success", "SuccessDark"),
        OrderStatus.Cancelled => ThemeColors.Resolve("Gray600", "Gray400"),
        OrderStatus.InProgress => ThemeColors.Resolve("WarningText", "WarningDark"),
        _ => ThemeColors.Resolve("Info", "InfoDark")
    };

    // ── Payment state ──────────────────────────────────────────────────────────────────────────
    // Derived from the model the same way StatusColor is: a computed property on the row, not a
    // converter. The pictogram binds PaymentGlyph (the mark) and PaymentColor (its fill); the
    // visible line and the accessible description carry the wording, so the state is never told
    // by colour alone.

    public PaymentState PaymentState => Model.PaymentState;

    /// <summary>
    /// True when the card offers to take a payment: the order is not fully paid. The board only
    /// lists active orders, so not-paid is the whole condition here.
    /// </summary>
    public bool CanCollectPayment => Model.PaymentState is not PaymentState.Paid;

    public Geometry PaymentGlyph => Model.PaymentState switch
    {
        PaymentState.Paid => PaidGlyph,
        PaymentState.PartiallyPaid => PartialGlyph,
        _ => UnpaidGlyph
    };

    /// <summary>
    /// The payment tint: the pictogram's fill and the payment line's colour.
    /// </summary>
    /// <remarks>
    /// Was <c>Microsoft.Maui.Graphics.Colors.Green/Orange/Red</c>. The pictogram is a 24dp mark
    /// with no wording of its own, and <c>Colors.Red #FF0000</c> on a <c>SurfaceVariantDark
    /// #2D2D2D</c> card was 3.44:1 — the state an operator most needs to notice was the hardest
    /// one to notice. From the palette it is 6.84:1, 7.96:1 and 6.91:1 for paid / partial /
    /// unpaid respectively.
    /// <para>
    /// The light branch is not a transcription of the dark one. Partial payment used
    /// <c>Warning #ED6C02</c> there, because the palette had no warm tone dark enough to
    /// serve as text: it is 3.11:1 on the light card #FFFFFF and 2.98:1 on #FAFAFA, both
    /// under the 4.5:1 owed at 12pt bold. <c>WarningText #A35B00</c> replaces it at
    /// 5.18:1 and 4.96:1, so the partial state is finally legible in the light theme. On
    /// the light card the full set now reads 5.13:1 paid, 5.18:1 partial, 4.98:1 unpaid;
    /// the dark branch is untouched.
    /// </para>
    /// <para>
    /// As with <see cref="StatusColor"/>, the wording is what carries the state:
    /// <see cref="PaymentText"/> spells it out and <see cref="PaymentHint"/> is the description,
    /// with <see cref="PaymentGlyph"/> a fourth, non-colour signal.
    /// </para>
    /// </remarks>
    public Color PaymentColor => Model.PaymentState switch
    {
        PaymentState.Paid => ThemeColors.Resolve("Success", "SuccessDark"),
        PaymentState.PartiallyPaid => ThemeColors.Resolve("WarningText", "WarningDark"),
        _ => ThemeColors.Resolve("Danger", "DangerDark")
    };

    /// <summary>
    /// The visible payment line. A partial payment says what is still owed, so it reads
    /// differently from an unpaid one.
    /// </summary>
    public string PaymentText => Model.PaymentState switch
    {
        PaymentState.Paid => "Оплачен",
        PaymentState.PartiallyPaid => $"Оплачен частично · осталось {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}",
        _ => $"Не оплачен · к оплате {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}"
    };

    /// <summary>
    /// The pictogram's accessible description: the full sentence, including what is still owed.
    /// </summary>
    public string PaymentHint => Model.PaymentState switch
    {
        PaymentState.Paid => "Заказ оплачен полностью",
        PaymentState.PartiallyPaid => $"Заказ оплачен частично, осталось {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}",
        _ => $"Заказ не оплачен, к оплате {TextFormat.Money(Money.FromKopecks(Model.BalanceKopecks))}"
    };

    public string NextActionText => Model.Status == OrderStatus.InProgress ? "Готов" : "Закрыть";
    public bool CanAdvance => Model.Status is OrderStatus.InProgress or OrderStatus.Ready;

    /// <summary>
    /// What the cancel button says on this card. A paid order is not just being closed off — money
    /// is going back to the customer, and the label is the only place that can say so before the
    /// operator taps.
    /// </summary>
    /// <remarks>
    /// Written out per row rather than put in a resource, because the two variants differ and a
    /// resource lookup cannot branch. It reads in the imperative so it fits the buttons it sits
    /// between ("Подробнее" / "Готов"), which are also per-row wording.
    /// </remarks>
    public string CancelText => Model.PaidKopecks > 0
        ? "Отменить и вернуть деньги"
        : "Отменить заказ";

    /// <summary>
    /// The pictogram-free sentence for the cancel button, carrying the amount that would go back.
    /// The button's own text says a return happens; this says how much, for a screen reader.
    /// </summary>
    public string CancelHint => Model.PaidKopecks > 0
        ? $"Отменить заказ и вернуть клиенту {TextFormat.Money(Money.FromKopecks(Model.PaidKopecks))}"
        : "Отменить заказ";
    /// <summary>
    /// Whether the cancel button is offered at all.
    /// </summary>
    /// <remarks>
    /// Open AND closed orders alike, paid or not. A paid order is cancellable now: cancelling
    /// voids the sale AND refunds the whole collected amount in the same transaction, so the cash
    /// goes back to the customer and the order leaves the revenue — which is the only correct
    /// outcome for a sale that should never have happened at all.
    /// <para>
    /// This used to carry <c>!Model.IsFullyPaid</c>, which was a UI workaround for a domain guard
    /// that has since been removed along with the guard itself. The comment it replaced is worth
    /// keeping in mind, because its reasoning was sound for what the domain did then: with no way
    /// to put money back, a cancel button on a paid order was a control that could only fail, and a
    /// failed cancel cost three steps (tap, confirm, reason) to reach "Нельзя отменить оплаченный
    /// заказ". It was also a hole around the payment block — cancelling was one tap and paying was
    /// three, so under queue pressure cancel was the tempting one, and the sale silently vanished
    /// from the shift report while the cash stayed in the drawer. Cancelling a paid order is now a
    /// working, money-moving operation, so neither cost applies.
    /// </para>
    /// <para>
    /// Note that <see cref="Order.IsFullyPaid"/> was never the same predicate as the domain guard
    /// it shadowed, which was <c>PaidKopecks &gt; 0</c>. A partially paid order has
    /// <c>IsFullyPaid == false</c> and so passed the UI, then failed in the domain — the button
    /// appeared and cancelled nothing. That inconsistency is gone with the guard.
    /// </para>
    /// </remarks>
    public bool CanCancel => Model.Status is not OrderStatus.Cancelled;
    public decimal TotalPrice => Model.TotalPrice;

    /// <summary>
    /// The total already carrying the active currency, e.g. "740,00 ₽" or "740,00 ₿".
    /// </summary>
    /// <remarks>
    /// This exists because the order card used to format its total with a
    /// <c>StringFormat="Итого: {0:F2} ₽"</c> in XAML, and that hard-codes both the symbol and the
    /// digit count — neither of which can be bound. Formatting moves here so it can read the
    /// selected currency; the label keeps the "Итого:" prefix so the wording stays in the view.
    /// </remarks>
    public string TotalPriceText => TextFormat.Money(TotalPrice);

    /// <summary>
    /// Re-raises <see cref="TotalPriceText"/> after the currency setting changed.
    /// </summary>
    /// <remarks>
    /// The card also renders a payment-state sentence that quotes money
    /// (<see cref="PaymentHint"/>, <see cref="PaymentLine"/>), so those are re-raised with it — a
    /// card where the headline total moved to ₿ while the line under it still read ₽ would be worse
    /// than one that had not updated at all.
    /// </remarks>
    public void RefreshMoneyText()
    {
        OnPropertyChanged(nameof(TotalPriceText));
        OnPropertyChanged(nameof(PaymentHint));
        OnPropertyChanged(nameof(PaymentLine));
    }

    /// <summary>Timestamps are stored in UTC and rendered in the local time zone.</summary>
    public string CreatedAtText => Model.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    /// <summary>The order's lines, projected into the display shape.</summary>
    /// <remarks>
    /// This is a LINQ iterator, not an <c>INotifyCollectionChanged</c> collection, and
    /// BindableLayout only listens to INPC. The obvious conclusion — the lines are built
    /// once and never move again — is wrong here, so read this before "fixing" it.
    /// <para>
    /// The row is RE-CREATED on each of the four collection actions BindableLayout
    /// handles, Move included: it applies every one of them through
    /// <c>NotifyCollectionChangedEventArgsExtensions.Apply</c>, where <c>Move</c> is a
    /// <c>removeAt</c> + <c>insert</c> and <c>insert</c> calls <c>CreateItemView</c>. The
    /// iterator is re-read each time, so the freshness does not depend on the row
    /// instance surviving a change to the order. A recycling <c>CollectionView</c> would
    /// be safe for the same reason: a new <c>BindingContext</c> is a new
    /// <c>ItemsSource</c>, so <c>CreateChildren()</c> runs again.
    /// </para>
    /// <para>
    /// Two things WOULD break it, and both are easy to reach by accident:
    /// </para>
    /// <list type="bullet">
    /// <item>a row instance that outlives a change to the set of lines — that is, any board
    /// action that alters the order's lines without raising <c>ItemLines</c> and without
    /// running its own <c>ReloadAsync</c>. Today every such action reloads the board, but
    /// that is a fact about the CALLERS and is written down nowhere in this property;</item>
    /// <item>a caller that bypasses <c>SyncWith</c> for this list, since the refresh guards
    /// — including the one in the <c>Move</c> branch — live inside <c>SyncWith</c> and
    /// nothing else stands behind them.</item>
    /// </list>
    /// </remarks>
    public IEnumerable<OrderItemLine> ItemLines => Model.Items.Select(item => new OrderItemLine(
        $"{item.ProductName}{(string.IsNullOrWhiteSpace(item.SelectedVariantName) ? string.Empty : $" [{item.SelectedVariantName}]")}" +
        $"{(string.IsNullOrWhiteSpace(item.SelectedModifierName) ? string.Empty : $" ({item.SelectedModifierName})")}",
        item.Quantity));
}

/// <summary>
/// One order line, split so the name and the quantity can sit in their own columns.
/// </summary>
/// <remarks>
/// These were a single preformatted string, so a long name pushed "× 1" onto a second line
/// and it landed alone under the name — measured on the emulator at 158dp, where
/// "Капучино (Овсяное) × 1" wrapped and orphaned its quantity. A trailing quantity is a
/// column of its own, never a wrap casualty.
/// </remarks>
public sealed record OrderItemLine(string Name, int Quantity);

public partial class OrdersViewModel : ObservableObject
{
    private readonly IOrderOperations orders;
    private readonly AppSettings settings;
    private readonly INavigationService navigation;
    private readonly IDialogService dialogs;
    private readonly IHapticService haptics;
    private readonly IPaymentSheet paymentSheet;
    private readonly IStockDispositionSheet stockDisposition;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrdersViewModel> logger;

    private readonly SemaphoreSlim loadGate = new(1, 1);
    private CancellationTokenSource? refreshCancellation;
    private Task? refreshTask;

    public OrdersViewModel(
        IOrderOperations orders,
        AppSettings settings,
        INavigationService navigation,
        IDialogService dialogs,
        IHapticService haptics,
        IPaymentSheet paymentSheet,
        IStockDispositionSheet stockDisposition,
        TimeProvider timeProvider,
        ILogger<OrdersViewModel> logger)
    {
        this.orders = orders;
        this.settings = settings;
        this.navigation = navigation;
        this.dialogs = dialogs;
        this.haptics = haptics;
        this.paymentSheet = paymentSheet;
        this.stockDisposition = stockDisposition;
        this.timeProvider = timeProvider;
        this.logger = logger;

        // AllowConcurrentExecutions: RefreshView.IsRefreshing is bound to IsBusy, so setting
        // IsBusy = true re-triggers LoadCommand. Without AllowConcurrentExecutions the command
        // would reject the re-entrant Execute call and the RefreshView spinner could get stuck.
        // LoadAsync also has an `if (IsBusy) return;` guard to prevent the feedback loop from
        // re-executing the Orders query on every cycle (infinite loading).
        LoadCommand = new AsyncRelayCommand(LoadAsync, options: AsyncRelayCommandOptions.AllowConcurrentExecutions);
        AdvanceStatusCommand = new AsyncRelayCommand<OrderRowViewModel>(AdvanceStatusAsync);
        OpenDetailsCommand = new AsyncRelayCommand<OrderRowViewModel>(OpenDetailsAsync);
        CancelOrderCommand = new AsyncRelayCommand<OrderRowViewModel>(CancelOrderAsync);
        CollectPaymentCommand = new AsyncRelayCommand<OrderRowViewModel>(CollectPaymentAsync);
        SelectTabCommand = new RelayCommand<OrderTab>(SelectTab);
    }

    /// <summary>
    /// Orders still being made, soonest arrival first, with the pre-orders pinned to the BOTTOM.
    /// </summary>
    /// <remarks>
    /// The owner chose two things here that a single filtered list could not do at once. The sort is
    /// <c>CreatedAt</c> — arrival — because the board's job is handing orders over in the order they
    /// came, and the promised time is already printed on the card. And a pre-order appears here as well
    /// as on its own tab, at the bottom: a café wants to start a «К 18:00» order early, and hiding it
    /// until the hour arrives means finding out too late. It sits below everything already being made
    /// because nothing is owed to that customer yet.
    /// </remarks>
    public ObservableCollection<OrderRowViewModel> PreparingOrders { get; } = [];

    /// <summary>
    /// Orders finished and waiting to be handed over, by arrival.
    /// </summary>
    /// <remarks>
    /// Sorted by arrival rather than by the moment they became ready, which is the owner's explicit
    /// instruction: the queue at the counter is worked in the order people paid, not the order the
    /// kitchen happened to finish them.
    /// </remarks>
    public ObservableCollection<OrderRowViewModel> ReadyOrders { get; } = [];

    /// <summary>Pre-orders with a time still in the future, soonest promise first.</summary>
    public ObservableCollection<OrderRowViewModel> ScheduledOrders { get; } = [];

    /// <summary>
    /// Tab captions with their counts. The count is on the tab rather than inside it because the reason
    /// to look is arithmetic — is anything waiting — and reading that off a tab bar costs nothing,
    /// while discovering it by opening the tab costs the operator their place in the queue.
    /// </summary>
    public string PreparingTabText => $"Готовятся · {PreparingOrders.Count}";
    public string ReadyTabText => $"Ждут выдачи · {ReadyOrders.Count}";
    public string ScheduledTabText => $"По времени · {ScheduledOrders.Count}";

    /// <summary>
    /// Whether any finished order is still unopened — the dot on the «Ждут выдачи» chip.
    /// </summary>
    /// <remarks>
    /// The owner's addition, and it is the piece that makes the COUNT usable. A count says how many are
    /// waiting; it does not say whether the operator already knows. Without this, «Ждут выдачи · 1» looks
    /// identical whether that order finished thirty seconds ago or has been sitting there all morning —
    /// and the operator has to open the tab to find out, which is the exact trip the count was meant to
    /// save. This is Toast's tab dot, and it carries the same fact the row dot carries one level up: the
    /// tab says there is something new, the row says which.
    /// <para>
    /// Computed from the rows rather than counted separately, so it cannot disagree with the dots drawn
    /// on the cards. Announced by <c>AnnounceTabCounts</c> on every load, because the rows are rebuilt on
    /// the auto-refresh tick and a row that has just been opened has to take the chip dot down with it.
    /// </para>
    /// </remarks>
    public bool HasUnseenReady
    {
        get
        {
            foreach (var row in ReadyOrders)
            {
                if (row.IsUnseenReady) return true;
            }

            return false;
        }
    }

    /// <summary>Why each tab is empty, so a zero is a sentence and not a blank screen.</summary>
    public string PreparingEmptyText => "Готовящихся заказов нет.";
    public string ReadyEmptyText => "Ничего не ждёт выдачи.";
    public string ScheduledEmptyText => "Предзаказов на будущее нет.";

    /// <summary>
    /// Which tab is showing. <see cref="OrderTab.Preparing"/> is the default, and that is the owner's
    /// instruction: for a single operator the queue is worked from what is being made, and the count on
    /// «Ждут выдачи» says whether anything has finished without anyone having to look into the tab.
    /// </summary>
    private OrderTab selectedTab = OrderTab.Preparing;
    public OrderTab SelectedTab
    {
        get => selectedTab;
        private set
        {
            if (!SetProperty(ref selectedTab, value)) return;
            OnPropertyChanged(nameof(IsPreparingTab));
            OnPropertyChanged(nameof(IsReadyTab));
            OnPropertyChanged(nameof(IsScheduledTab));
        }
    }

    public bool IsPreparingTab => SelectedTab == OrderTab.Preparing;
    public bool IsReadyTab => SelectedTab == OrderTab.Ready;
    public bool IsScheduledTab => SelectedTab == OrderTab.Scheduled;

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    private string message = string.Empty;
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> AdvanceStatusCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> OpenDetailsCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> CancelOrderCommand { get; }
    public IAsyncRelayCommand<OrderRowViewModel> CollectPaymentCommand { get; }

    /// <summary>Switches the visible tab. Takes the <see cref="OrderTab"/>, bound through x:Static.</summary>
    public IRelayCommand<OrderTab> SelectTabCommand { get; }
}
