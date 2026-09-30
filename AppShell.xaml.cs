using CafePosApp.Diagnostics;
using CafePosApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CafePosApp
{
    public partial class AppShell : Shell
    {
        public AppShell(IServiceProvider services)
        {
            InitializeComponent();
            AppLog.Info("AppShell constructor started");

            // Pages are created lazily (ContentTemplate). Previously all six pages were
            // constructed — XAML inflation included — before the first frame was rendered.
            AddPage("Меню", "menu", "☰", () => services.GetRequiredService<MenuPage>());
            AddPage("Заказы", "orders", "📋", () => services.GetRequiredService<OrdersPage>());
            AddPage("Смена", "shift", "🕐", () => services.GetRequiredService<ShiftReportPage>());
            AddPage("Аналитика", "shift-analytics", "📊", () => services.GetRequiredService<ShiftAnalyticsPage>());
            AddPage("Каталог", "catalog", "📂", () => services.GetRequiredService<CatalogManagementPage>());
            AddPage("Настройки", "settings", "⚙", () => services.GetRequiredService<SettingsPage>());

            Routing.RegisterRoute("order-details", typeof(OrderDetailsPage));
        }

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
