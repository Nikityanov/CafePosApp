using CafePos.Core.Services;
using CafePosApp.Diagnostics;
using CafePosApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp
{
    public partial class AppShell : Shell
    {
        /// <summary>The one route reachable while no shift is open. Matched literally, not by prefix.</summary>
        private const string OpenShiftRoute = "open-shift";

        private readonly IShiftSession shiftSession;

        public AppShell(IServiceProvider services, IShiftSession shiftSession)
        {
            InitializeComponent();
            this.shiftSession = shiftSession;
            AppLog.Info("AppShell constructor started");

            // Pages are created lazily (ContentTemplate). Previously all six pages were
            // constructed — XAML inflation included — before the first frame was rendered.
            // Glyph choice is empirical, not decorative. 📋 🕐 📊 are emoji-presentation
            // characters with no monochrome outline in the bundled font, so on the tab bar they
            // collapsed into unreadable solid blocks (measured: Заказы -> filled square,
            // Смена -> filled circle, Аналитика -> filled square). ☰ 📂 ⚙ are covered and
            // render as real outlines, which is why the same two glyphs looked correct inside
            // the overflow sheet. Anything added here must be checked on the tab bar itself.
            //
            // SIX TABS, AND THAT IS THE DECISION — the tab bar is not shortened to make the
            // overflow tab go away, because the overflow tab's TITLE is ours to set. Past five
            // items MAUI's Android tab bar moves the rest behind a built-in overflow tab, and its
            // title is read from the Android string resource `overflow_tab_title`: the library
            // ships it as " More ", and Platforms/Android/Resources/values/strings.xml overrides
            // it with «Ещё». So the last English word in the UI leaves without a single ShellItem
            // being touched, and all six sections stay one tap away at any screen width.
            //
            // The structural alternatives were weighed, and the reasons are worth keeping because
            // none of them is obvious:
            //
            //   · Moving the last three sections into a FlyoutItem needs a flyout to open, and the
            //     hamburger lives in the Shell nav bar — which every top-level page in this app
            //     hides (Shell.NavBarIsVisible="False" on all seven). The drawer would then be
            //     reachable only by an undocumented swipe from the screen edge.
            //   · A hub page instead would hide three sections behind two taps and cost a page plus
            //     a DI registration, and it would not remove the need for those routes anyway.
            //   · Shorter titles do nothing: the overflow tab is chosen by the NUMBER of items.
            //
            // WHAT MUST NOT CHANGE HERE. The routes menu, orders, shift, shift-analytics, catalog
            // and settings are load-bearing, and so is "///menu" as the way OFF the opening screen:
            // NavigationService.LeaveOpenShiftAsync pops ".." and then goes to "///menu", which is
            // what fixes the opening screen staying on the shift tab's stack over a shift that has
            // just been opened (BUGS-cash-register §2.3). ShiftReportViewModel opens the analytics
            // report with GoToTabAsync("shift-analytics"), i.e. "///shift-analytics". Each of those
            // sections must therefore keep being a ShellContent reachable from the root, so moving
            // one into a pushed page or behind a hub would break a caller this file cannot see.
            // If the section list ever changes, re-read this before moving anything.
            AddPage("Меню", "menu", "☰", () => services.GetRequiredService<MenuPage>());
            AddPage("Заказы", "orders", "≡", () => services.GetRequiredService<OrdersPage>());
            AddPage("Смена", "shift", "◷", () => services.GetRequiredService<ShiftReportPage>());
            AddPage("Аналитика", "shift-analytics", "◫", () => services.GetRequiredService<ShiftAnalyticsPage>());
            AddPage("Каталог", "catalog", "📂", () => services.GetRequiredService<CatalogManagementPage>());
            AddPage("Настройки", "settings", "⚙", () => services.GetRequiredService<SettingsPage>());

            Routing.RegisterRoute("order-details", typeof(OrderDetailsPage));
            Routing.RegisterRoute(OpenShiftRoute, typeof(OpenShiftPage));

            Navigating += OnNavigating;
        }

        /// <summary>
        /// Refuses every destination but the opening screen while no shift is open.
        /// </summary>
        /// <remarks>
        /// A till that has never been told what change it holds cannot be used to sell, and the
        /// alternative — letting the operator reach the menu and discovering the problem at the till,
        /// after building a cart — is the version of this that wastes their time.
        /// <para>
        /// The check is against a CACHE, not a query: this callback is synchronous and the database is
        /// not. <c>IShiftSession</c> is refreshed after the migration at startup and by the two
        /// operations that change the answer, which are the only two there are.
        /// </para>
        /// <para>
        /// The refused navigation is REPLACED rather than merely cancelled. Cancelling alone would
        /// leave the operator where they were — still on the menu, still able to see it — and the
        /// request that was refused would be silently dropped.
        /// </para>
        /// </remarks>
        private void OnNavigating(object? sender, ShellNavigatingEventArgs e)
        {
            if (shiftSession.IsShiftOpen) return;
            if (IsOpenShift(e.Target)) return;

            e.Cancel();
            if (IsOpenShift(e.Current)) return;

            // Re-entrant: this fires the same event again, and the line above stops the loop because
            // the target is now the opening route.
            Dispatcher.Dispatch(async () => await Shell.Current.GoToAsync(OpenShiftRoute));
        }

        /// <summary>
        /// Whether a navigation destination is the opening screen.
        /// </summary>
        /// <remarks>
        /// The route string is compared, not searched: <c>///menu/open-shift</c> and a future
        /// <c>open-shift-confirm</c> must both fail this, or the guard becomes a suggestion.
        /// </remarks>
        private static bool IsOpenShift(ShellNavigationState state) =>
            string.Equals(state?.Location?.OriginalString, OpenShiftRoute, StringComparison.Ordinal);

        private void AddPage(string title, string route, string iconGlyph, Func<ContentPage> createPage)
        {
            var tabBar = Items.OfType<TabBar>().FirstOrDefault();
            if (tabBar is null)
            {
                tabBar = new TabBar();
                Items.Add(tabBar);
            }

            tabBar.Items.Add(new ShellContent
            {
                Title = title,
                Route = route,
                ContentTemplate = new DataTemplate(createPage),
                Icon = new FontImageSource
                {
                    Glyph = iconGlyph,
                    Size = 22,
                    // Was Colors.Gray: a fixed value that ignored the theme and overrode the
                    // Shell.TabBar* colours set in Styles.xaml. Leaving it null lets the shell
                    // tint the glyph to match the unselected/selected tab colours.
                    Color = null
                }
            });
        }
    }
}
