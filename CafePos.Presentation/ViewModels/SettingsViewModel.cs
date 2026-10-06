using System.Collections.ObjectModel;
using CafePos.Core.Common;
using CafePos.Core.Services;
using CafePos.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;

namespace CafePos.Presentation.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettings settings;
    private readonly IBackupService backups;
    private readonly IFileService files;

    /// <summary>The catalogue service, for the CSV round-trip in <c>SettingsViewModel.Catalog.cs</c>.</summary>
    /// <remarks>
    /// Added when the CSV import/export moved off the catalogue screen. It was a constructor
    /// parameter of <c>CatalogManagementViewModel</c> purely for <c>IFileService</c> — that
    /// dependency is gone from there — and both services here are singletons registered by
    /// <c>MauiProgram</c> (<c>ICatalogService</c> via <c>AddCafePosCore</c>, <c>IHapticService</c>
    /// directly), so this transient ViewModel resolves them without a registration change.
    /// </remarks>
    private readonly ICatalogService catalog;
    private readonly IHapticService haptics;
    private readonly IDialogService dialogs;
    private readonly ILogger<SettingsViewModel> logger;

    public SettingsViewModel(
        IAppSettings settings,
        IBackupService backups,
        IFileService files,
        ICatalogService catalog,
        IHapticService haptics,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        this.settings = settings;
        this.backups = backups;
        this.files = files;
        this.catalog = catalog;
        this.haptics = haptics;
        this.dialogs = dialogs;
        this.logger = logger;

        CafeName = settings.CafeName;
        AutoRefreshSecondsText = settings.AutoRefreshSeconds.ToString();
        OrderPrefix = settings.OrderPrefix;
        ThemeText = settings.Theme switch { ThemePreference.Light => LightTitle, ThemePreference.Dark => DarkTitle, _ => SystemTitle };
        SelectedCurrency = settings.CurrencyCode;

        SaveCommand = new RelayCommand(Save);
        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync);
        ExportDataCommand = new AsyncRelayCommand(ExportDataAsync);
        RestoreBackupCommand = new AsyncRelayCommand(RestoreBackupAsync);
        ShareLogCommand = new AsyncRelayCommand(ShareLogAsync);
        LoadBackupsCommand = new AsyncRelayCommand(LoadBackupsAsync);

        // The catalogue CSV round-trip, moved here from CatalogManagementViewModel. Assigned here
        // rather than in the partial so every command this ViewModel owns is visible in one list.
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync);
        ImportCsvCommand = new AsyncRelayCommand(ImportCsvAsync);
    }

    private string cafeName = string.Empty;
    public string CafeName { get => cafeName; set => SetProperty(ref cafeName, value); }

    private string autoRefreshSecondsText = string.Empty;
    public string AutoRefreshSecondsText { get => autoRefreshSecondsText; set => SetProperty(ref autoRefreshSecondsText, value); }

    private string orderPrefix = string.Empty;
    public string OrderPrefix { get => orderPrefix; set => SetProperty(ref orderPrefix, value); }

    /// <summary>
    /// The three titles, named once. The read below and the write in <see cref="Save"/> both switch
    /// over them, and repeating six Russian literals across two switches is how a translated title
    /// ends up recognised on the way in and not on the way out — which would save the wrong theme and
    /// report success.
    /// </summary>
    private const string SystemTitle = "Системная";
    private const string LightTitle = "Светлая";
    private const string DarkTitle = "Темная";

    private string themeText = SystemTitle;
    public string ThemeText { get => themeText; set => SetProperty(ref themeText, value); }

    public IReadOnlyList<string> Themes { get; } = [SystemTitle, LightTitle, DarkTitle];

    // ── Currency ───────────────────────────────────────────────────────────────────────────────
    // A Picker over the preset list. It is bound to a Currency OBJECT rather than to the ISO code
    // string, because ItemDisplayBinding renders the title while SelectedItem needs the item
    // itself — binding SelectedItem to a string would have to match titles, and the titles are
    // Russian text a future translation pass could change. The code, not the title, is what gets
    // persisted; see Save().
    //
    // The property is named SelectedCurrency, not Currency, on purpose: a property called Currency
    // would shadow the type of the same name inside this class, which is the sort of collision that
    // compiles until someone adds a second member and then does not.

    private Currency selectedCurrency = Currencies.Ruble;

    /// <summary>The currency the picker has selected. Raises <see cref="CurrencySummary"/>.</summary>
    /// <remarks>
    /// The assigned value is re-resolved through <see cref="Currencies.FromCode"/> rather than
    /// stored as given. That guarantees the selection is always one of the instances in
    /// <see cref="CurrencyList"/>, which is what the Picker's SelectedItem match needs, and it
    /// means a null or unrecognised value degrades to the ruble rather than leaving the picker
    /// pointing at nothing.
    /// </remarks>
    public Currency SelectedCurrency
    {
        get => selectedCurrency;
        set
        {
            var resolved = Currencies.FromCode(value?.Code);
            if (!SetProperty(ref selectedCurrency, resolved)) return;
            OnPropertyChanged(nameof(CurrencySummary));
        }
    }

    /// <summary>Every preset, for the picker.</summary>
    public IReadOnlyList<Currency> CurrencyList { get; } = Currencies.All;

    /// <summary>
    /// What the selected currency will actually look like: its name, a worked example, and its
    /// minor unit.
    /// </summary>
    /// <remarks>
    /// The worked example is the point. An operator cannot tell from the title alone whether the
    /// sign they picked is the one they wanted, and the two currencies that share a sign — CNY ¥
    /// and JPY ¥ — are told apart ONLY by their decimal places. So the example is rendered through
    /// the real formatter at a value with a non-zero fraction, which makes "1235 ¥" (yen, no minor
    /// unit) visibly different from "1234,50 ¥" (yuan). The minor-unit name is spelled out for the
    /// same reason: "капеек" is what tells a Belarusian operator the fraction really is kopecks.
    /// </remarks>
    public string CurrencySummary
    {
        get
        {
            var example = TextFormat.Money(1234.5m, selectedCurrency);
            var minor = selectedCurrency.MinorUnitDigits == 0
                ? "без дробной части"
                : $"дробная часть — {selectedCurrency.MinorUnitName}";
            return $"{selectedCurrency.Title} · {example} · {minor}";
        }
    }

    /// <summary>Local backups, newest first (created automatically, on shift close and manually).</summary>
    public ObservableCollection<BackupInfo> Backups { get; } = [];

    private string message = string.Empty;

    /// <summary>
    /// Severity of <see cref="Message"/>. The enum is named <c>MessageLevel</c> and the property
    /// <c>MessageSeverity</c>, because a nested enum may not share its name with a property of the
    /// declaring class (CS0102) — the same split <c>CatalogManagementViewModel</c> records between
    /// its <c>NoticeLevel</c> enum and its <c>NoticeSeverity</c> property.
    /// </summary>
    public enum MessageLevel { None, Success, Error }

    private MessageLevel messageLevel = MessageLevel.None;
    public MessageLevel MessageSeverity => messageLevel;
    public string Message => message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(message);

    /// <summary>
    /// The colour of <see cref="Message"/>. Computed from the level through
    /// <see cref="ThemeColors"/> rather than bound as an <c>AppThemeBinding</c>, for the reason
    /// <c>OrdersViewModel.StatusColor</c> gives: <c>AppThemeBinding</c> only governs values that
    /// come from XAML, so a colour resolved there cannot switch on a bound property. This is the
    /// app's existing answer, not a new one.
    /// </summary>
    /// <remarks>
    /// Success was 3.25:1 on a SurfaceDark card before this — SuccessDark is 8.28:1 — and the
    /// label was worse than low-contrast: it was hard-wired green while carrying "Не удалось …"
    /// from six failure paths. Nothing here is colour-only: the message text always says what
    /// happened ("Настройки сохранены.", "Файл не подходит: …"), so the colour is a second cue on
    /// top of a sentence, never the message itself.
    /// </remarks>
    public Color MessageColor => MessageSeverity switch
    {
        MessageLevel.Error => PaletteAccess.Resolve("Danger", "DangerDark"),
        _ => PaletteAccess.Resolve("Success", "SuccessDark")
    };

    /// <summary>
    /// Sets the message and its severity together, so the two can never disagree — the reason
    /// <c>Message</c> has no public setter despite being a public property.
    /// </summary>
    private void SetMessage(string text, MessageLevel level)
    {
        // The severity is applied unconditionally, before the text's own change is even known.
        // The obvious `if (!SetProperty(...)) return;` guard would leave the label green when the
        // same sentence arrives twice and the second time it is a failure — two exports in a row
        // both writing "Архив сохранён: …", or the same validation error for two files.
        messageLevel = level;
        OnPropertyChanged(nameof(MessageSeverity));
        OnPropertyChanged(nameof(MessageColor));

        // Only the text can flip IsVisible, so this one is safe to skip when it did not change.
        if (SetProperty(ref message, text)) OnPropertyChanged(nameof(HasMessage));
    }

    private bool isBusy;
    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }

    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand CreateBackupCommand { get; }
    public IAsyncRelayCommand ExportDataCommand { get; }
    public IAsyncRelayCommand RestoreBackupCommand { get; }
    public IAsyncRelayCommand ShareLogCommand { get; }
    public IAsyncRelayCommand LoadBackupsCommand { get; }

    private void Save()
    {
        // Every failure path is Error and the single success is Success. They used to share one
        // untyped string, which is why all four of them rendered green.
        if (string.IsNullOrWhiteSpace(CafeName)) { SetMessage("Укажите название кафе.", MessageLevel.Error); return; }
        if (!int.TryParse(AutoRefreshSecondsText, out var interval) || interval is < 5 or > 300) { SetMessage("Интервал обновления должен быть от 5 до 300 секунд.", MessageLevel.Error); return; }
        if (string.IsNullOrWhiteSpace(OrderPrefix)) { SetMessage("Укажите префикс номера заказа.", MessageLevel.Error); return; }

        settings.CafeName = CafeName;
        settings.AutoRefreshSeconds = interval;
        settings.OrderPrefix = OrderPrefix;
        settings.Theme = ThemeText == LightTitle ? ThemePreference.Light
            : ThemeText == DarkTitle ? ThemePreference.Dark
            : ThemePreference.System;
        settings.ApplyTheme();

        // Persisted BY CODE, not by title: the titles are Russian strings that a future translation
        // pass could change, and the stored value has to survive that. CurrencySelection also
        // publishes the new value as Currencies.Default, which is what every formatter in the app
        // reads from here on.
        settings.CurrencyCode = SelectedCurrency;

        SetMessage("Настройки сохранены.", MessageLevel.Success);
    }
}
