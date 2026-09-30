using System.Collections.ObjectModel;
using CafePos.Core.Services;
using CafePosApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace CafePosApp.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings settings;
    private readonly IBackupService backups;
    private readonly IFileService files;
    private readonly IDialogService dialogs;
    private readonly ILogger<SettingsViewModel> logger;

    public SettingsViewModel(
        AppSettings settings,
        IBackupService backups,
        IFileService files,
        IDialogService dialogs,
        ILogger<SettingsViewModel> logger)
    {
        this.settings = settings;
        this.backups = backups;
        this.files = files;
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
    public string Message { get => message; private set { if (SetProperty(ref message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

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
        if (string.IsNullOrWhiteSpace(CafeName)) { Message = "Укажите название кафе."; return; }
        if (!int.TryParse(AutoRefreshSecondsText, out var interval) || interval is < 5 or > 300) { Message = "Интервал обновления должен быть от 5 до 300 секунд."; return; }
        if (string.IsNullOrWhiteSpace(OrderPrefix)) { Message = "Укажите префикс номера заказа."; return; }

        settings.CafeName = CafeName;
        settings.AutoRefreshSeconds = interval;
        settings.OrderPrefix = OrderPrefix;
        settings.Theme = ThemeText switch { "Светлая" => AppTheme.Light, "Темная" => AppTheme.Dark, _ => AppTheme.Unspecified };
        settings.ApplyTheme();
        Message = "Настройки сохранены.";
    }
}
