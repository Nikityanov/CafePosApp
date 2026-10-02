using System.Collections.ObjectModel;
using CafePos.Core.Services;
// ThemeColors lives in the Converters namespace only because Controls/ResourceStyles.cs was out of
// the change that added it; it belongs beside ResourceStyles.TryGetColor. See its own remarks.
using CafePosApp.Converters;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;

namespace CafePosApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings settings;
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
        AppSettings settings,
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
        ThemeText = settings.Theme switch { AppTheme.Light => "Светлая", AppTheme.Dark => "Темная", _ => "Системная" };

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

    private string themeText = "Системная";
    public string ThemeText { get => themeText; set => SetProperty(ref themeText, value); }

    public IReadOnlyList<string> Themes { get; } = ["Системная", "Светлая", "Темная"];

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
        MessageLevel.Error => ThemeColors.Resolve("Danger", "DangerDark"),
        _ => ThemeColors.Resolve("Success", "SuccessDark")
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
        settings.Theme = ThemeText switch { "Светлая" => AppTheme.Light, "Темная" => AppTheme.Dark, _ => AppTheme.Unspecified };
        settings.ApplyTheme();
        SetMessage("Настройки сохранены.", MessageLevel.Success);
    }
}
