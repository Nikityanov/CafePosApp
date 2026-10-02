using CafePos.Core.Common;
using CafePos.Core.Models;
using CafePosApp.Diagnostics;
using CafePosApp.Services;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Devices;

namespace CafePosApp.Views;

/// <summary>
/// The one decision of a cancellation that the app cannot make for the operator: whether the
/// ingredients of the cancelled order come back to the shelf or stay written off.
/// </summary>
/// <remarks>
/// A bottom sheet through the same toolkit <c>Popup</c> as the payment sheet and the catalogue
/// action sheet, so the choice is presented by the mechanism the app already uses rather than by a
/// third one. Two options with a sentence each, not a radio pair, for the reason recorded on the
/// footnote in the markup.
/// <para>
/// Reuses the measured fixes from <see cref="CatalogActionSheetPopup"/>: the ScrollView carries
/// <c>VerticalOptions="Start"</c> (without it the sheet stretches to its height cap), the width is
/// forced in code because the toolkit rewrites <c>HorizontalOptions.Fill</c> to <c>Center</c>, and
/// the dim comes from <c>PopupOptions.PageOverlayColor</c> because the toolkit insets popup content
/// by 15dp per side.
/// </para>
/// </remarks>
[XamlCompilation(XamlCompilationOptions.Compile)]
public partial class StockDispositionSheetPopup : Popup<StockDispositionChoice?>
{
    public StockDispositionSheetPopup(StockDispositionSheetRequest request)
    {
        InitializeComponent();

        // Full-width bottom sheet. The toolkit converts HorizontalOptions.Fill back to Center, so
        // the width is set explicitly instead. The window width is used (not the physical display
        // width) so the sheet fits in split-screen/freeform windows.
        SizeToWindow();
        this.SizeChanged += (_, _) => SizeToWindow();

        // Start below the screen edge; Opened slides the sheet up into place.
        TranslationY = 1000;

        TitleLabel.Text = $"{request.OrderTitle} — продукты со склада";

        // The two options behave the same, so the sentence differs only in what is being taken
        // back: a paid cancellation also gives the money back, and the operator is deciding about
        // stock, not about cash.
        MoneyLabel.Text = request.Paid > 0
            ? $"Заказ на {TextFormat.Money(request.Total)}, вернётся {TextFormat.Money(request.Paid)}"
            : $"Заказ на {TextFormat.Money(request.Total)}, оплаты не было";

        LeadLabel.Text =
            "Отмена списывает продукты со склада. Ничего не готовили — остатки можно вернуть. " +
            "Готовили или отдали — остаются списанными.";

        // The footnote is the argument for the SECOND option, so it is only shown when the
        // operator could in fact pick it. On an unpaid order the discrepancy case is rarer but not
        // rarer than on a paid one, so it is shown for both; the sentence is written to make sense
        // either way.
        FootnoteLabel.Text =
            "При расхождении по складу остатки возвращать нельзя: вернётся уже неверное количество, " +
            "и расхождение сохранится до настоящей инвентаризации. Тогда выбирайте «Оставить списанными».";

        Opened += (_, _) =>
        {
            SizeToWindow();
            _ = this.TranslateToAsync(0, 0, 300, Easing.CubicOut);
        };
    }

    private async void OnReturnClicked(object? sender, EventArgs e) =>
        await CloseAsync(new StockDispositionChoice(StockDisposition.ReturnToStock));

    private async void OnLeaveClicked(object? sender, EventArgs e) =>
        await CloseAsync(new StockDispositionChoice(StockDisposition.LeaveWrittenOff));

    private async void OnDismissClicked(object? sender, EventArgs e) => await CloseAsync(null);

    /// <summary>
    /// Matches the sheet to the width and height of the window it is shown over, and lifts its
    /// content above the system navigation bar.
    /// </summary>
    /// <remarks>
    /// The height is only a cap, never a target: <c>MaximumHeightRequest</c> stops the ScrollView
    /// growing, and the ScrollView's own <c>VerticalOptions="Start"</c> is what makes it stop at
    /// its content.
    /// <para>
    /// The bottom inset is the real system navigation-bar height read from the platform, not a
    /// hand-picked number, for the reason PaymentSheetPopup records: the sheet is pinned to the
    /// window's bottom edge and on an edge-to-edge Android window the navigation bar covers that
    /// edge. Zero on platforms with no bottom bar.
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

            // Cap the sheet at 90% of the window height so the handle stays on a short landscape
            // window, and so a long option list cannot run off the top.
            var height = window?.Height ?? 0;
            if (height > 0 && SheetScroll != null)
            {
                SheetScroll.MaximumHeightRequest = height * 0.9;
            }

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
            AppLog.Exception("StockDispositionSheetPopup.SizeToWindow", exception);
        }
    }

    /// <summary>
    /// Height of the system navigation bar in device-independent pixels, or 0 when there is none.
    /// Android-only, the same read as PaymentSheetPopup's.
    /// </summary>
    private static double GetBottomInset()
    {
        try
        {
#if ANDROID
            var resources = Platform.CurrentActivity?.Resources;
            if (resources is not null)
            {
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