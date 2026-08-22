using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Baballonia.Assets;
using Baballonia.Contracts;
using Baballonia.Models;
using Baballonia.Services;
using Baballonia.Services.Inference;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using OscCore;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Baballonia.Views;

namespace Baballonia.ViewModels.SplitViewPane;

public partial class AppSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    [property: SavedSetting("AppSettings_RecalibrateAddress", "/avatar/parameters/etvr_recalibrate")]
    private string _recalibrateAddress;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_RecenterAddress", "/avatar/parameters/etvr_recenter")]
    private string _recenterAddress;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OSCPrefix", "")]
    private string _oscPrefix;

    [ObservableProperty]
    private IBrush _oscPrefixBackgroundColor;

    private bool _isOscPrefixValid = true;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroEnabled", true)]
    private bool _oneEuroMinEnabled;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroMinFreqCutoff", 0.5f)]
    private float _oneEuroMinFreqCutoff;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_OneEuroSpeedCutoff", 3f)]
    private float _oneEuroSpeedCutoff;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_UseDFR", false)]
    private bool _useDFR;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_UseGPU", true)]
    private bool _useGPU;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_SteamVRAutoStart", true)]
    private bool _steamvrAutoStart;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_CheckForUpdates", true)]
    private bool _checkForUpdates;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_ReceivePreReleaseUpdates", true)]
    private bool _receivePreReleaseUpdates;

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string _logLevel;

    public ObservableCollection<string> LowestLogLevel { get; } =
    [
        Resources.Settings_LogLevel_Debug,
        Resources.Settings_LogLevel_Information,
        Resources.Settings_LogLevel_Warning,
        Resources.Settings_LogLevel_Error
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebugMenuToggleVisible))]
    [property: SavedSetting("AppSettings_AdvancedOptions", false)]
    private bool _advancedOptions;

    /// <summary>
    /// The "Show Debug menu" toggle is desktop-only (the Debug page isn't reachable on mobile), so it
    /// shows only when Advanced is on <em>and</em> we're on a supported desktop OS.
    /// </summary>
    public bool DebugMenuToggleVisible => AdvancedOptions && Utils.IsSupportedDesktopOS;

    // Advanced-only options. Surfaced in Settings beneath the Advanced toggle and only visible while
    // AdvancedOptions is on (see AppSettingsView.axaml).
    [ObservableProperty]
    [property: SavedSetting("AppSettings_SplitEyeVideoSwap", false)]
    private bool _splitEyeVideoSwap;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_ShowDebugMenu", false)]
    private bool _showDebugMenu;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_StabilizeEyes", true)]
    private bool _stabilizeEyes;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_EyeWidenGain", 1.0f)]
    private float _eyeWidenGain;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_EyeSquintGain", 1.0f)]
    private float _eyeSquintGain;

    [ObservableProperty]
    [property: SavedSetting("AppSettings_EyeBrowGain", 1.0f)]
    private float _eyeBrowGain;

    [ObservableProperty] private bool _onboardingEnabled;

    public IOscTarget OscTarget { get; }

    private readonly FacePipelineManager _facePipelineManager;
    private readonly EyePipelineManager _eyePipelineManager;
    private readonly ILogger<AppSettingsViewModel> _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IAppUpdateService? _appUpdateService;
    private readonly OpenVRService? _openVrService;
    // Set once the constructor finishes loading persisted settings, so OnSteamvrAutoStartChanged
    // can tell a user toggle apart from the initial load (which must not touch OpenVR).
    private bool _settingsLoaded;

    public AppSettingsViewModel(
        FacePipelineManager facePipelineManager,
        EyePipelineManager eyePipelineManager,
        ILocalSettingsService localSettingsService,
        IOscTarget oscTarget,
        ParameterSenderService parameterSenderService,
        ILogger<AppSettingsViewModel> logger,
        IThemeSelectorService themeSelectorService,
        IAppUpdateService? appUpdateService = null)
    {
        OscTarget = oscTarget;
        _localSettingsService = localSettingsService;
        _facePipelineManager = facePipelineManager;
        _eyePipelineManager = eyePipelineManager;
        _logger = logger;
        _appUpdateService = appUpdateService;
        // OpenVRService is only registered on supported desktop OSes; resolve it optionally
        // so the SteamVR-autostart toggle works there and no-ops elsewhere (see null guard below).
        _openVrService = Ioc.Default.GetService<OpenVRService>();
        _localSettingsService.Load(this);

        LogLevel = _localSettingsService.ReadSetting("AppSettings_LogLevel", "Debug");

        // Restore the default when the persisted OSC output port is invalid.
        if (OscTarget.OutPort == 0)
        {
            const int port = 8888;
            OscTarget.OutPort = port;
            _localSettingsService.SaveSetting("OSCOutPort", port);
        }

        // Edge case: Update the OscPrefix Background color if and only if
        // The theme changes and the previous input WAS valid (IE keep red)
        themeSelectorService.ThemeChanged += variant =>
        {
            if (_isOscPrefixValid)
                SetOscPrefixBackgroundColor(variant);
        };

        OnboardingEnabled = Utils.IsSupportedDesktopOS;

        PropertyChanged += (_, p) =>
        {
            _localSettingsService.Save(this);
            _facePipelineManager.LoadFilter();
            _eyePipelineManager.LoadFilter();

            if (p.PropertyName == nameof(StabilizeEyes))
            {
                _eyePipelineManager.LoadEyeStabilization();
            }

            if (p.PropertyName == nameof(SplitEyeVideoSwap))
            {
                _eyePipelineManager.LoadSplitEyeSwap();
            }
        };

        _settingsLoaded = true;
    }

    public string PreReleaseUpdatesHeader => Text(
        "Settings_PreReleaseUpdates_Header",
        "Receive preview updates");

    public string PreReleaseUpdatesDescription => Text(
        "Settings_PreReleaseUpdates_Description",
        "Also offer experimental GitHub pre-releases.");

    public string UpdateNowHeader => Text("Settings_UpdateNow_Header", "Baballonia Next updates");

    public string UpdateNowDescription => Text(
        "Settings_UpdateNow_Description",
        "Check your Baballonia Next release channel manually.");

    public string UpdateNowButton => Text("Settings_UpdateNow_Button", "Check now");

    public string OpenReleasesButton => Text("Settings_Update_OpenReleases", "Open releases");

    private bool CanCheckForUpdatesNow() => !IsCheckingForUpdates && _appUpdateService is not null;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdatesNow))]
    private async Task CheckForUpdatesNowAsync()
    {
        if (_appUpdateService is null)
            return;

        IsCheckingForUpdates = true;
        UpdateStatus = Text("Settings_Update_Checking", "Checking for updates…");
        try
        {
            var result = await _appUpdateService.CheckForUpdatesAsync(ReceivePreReleaseUpdates);
            switch (result.State)
            {
                case AppUpdateCheckState.UpToDate:
                    UpdateStatus = Text("Settings_Update_UpToDate", "Baballonia Next is up to date.");
                    break;
                case AppUpdateCheckState.NotInstalled:
                    UpdateStatus = Text(
                        "Settings_Update_Portable",
                        "Automatic updates require the installed Setup version.");
                    break;
                case AppUpdateCheckState.Failed:
                    UpdateStatus = string.Format(
                        Text("Settings_Update_Failed", "Update check failed: {0}"),
                        result.ErrorMessage ?? "Unknown error");
                    break;
                case AppUpdateCheckState.UpdateAvailable:
                    UpdateStatus = string.Format(
                        Text("Settings_Update_Available", "Baballonia Next {0} is available."),
                        result.Version ?? "");
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                        { MainWindow: { } mainWindow })
                    {
                        await UpdateDialog.ShowAsync(mainWindow, _appUpdateService, result);
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Manual update check failed");
            UpdateStatus = string.Format(
                Text("Settings_Update_Failed", "Update check failed: {0}"),
                exception.Message);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    partial void OnIsCheckingForUpdatesChanged(bool value) =>
        CheckForUpdatesNowCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void OpenReleases()
    {
        if (_appUpdateService is not null)
            Utils.OpenUrl(_appUpdateService.ReleasesUri.ToString());
    }

    private static string Text(string key, string fallback) =>
        Resources.ResourceManager.GetString(key, Resources.Culture) ?? fallback;

    // Tell the navigation sidebar to add/remove the Debug page entry as soon as the toggle flips,
    // instead of only on next launch. The generic PropertyChanged handler above persists the value.
    partial void OnShowDebugMenuChanged(bool value)
    {
        WeakReferenceMessenger.Default.Send(new ShowDebugMenuChangedMessage(value));
    }

    partial void OnLogLevelChanged(string value)
    {
        var prev = _localSettingsService.ReadSetting("AppSettings_LogLevel", value);
        if (prev == value)
            return;

        var newLogLevel = value switch
        {
            var v when v == Resources.Settings_LogLevel_Debug => "Debug",
            var v when v == Resources.Settings_LogLevel_Information => "Information",
            var v when v == Resources.Settings_LogLevel_Warning => "Warning",
            var v when v == Resources.Settings_LogLevel_Error => "Error",
            _ => "Debug"
        };
        _localSettingsService.SaveSetting("AppSettings_LogLevel", newLogLevel);
    }

    partial void OnOscPrefixChanged(string value)
    {
        // 1) A valid OSC prefix is also a valid message itself
        // IE: /foo/bar + /cheekPuffLeft
        // 2) Empty strings are also valid, IE no prefix
        _isOscPrefixValid = OscMessage.TryParse(value, out _) || string.IsNullOrEmpty(value);

        if (_isOscPrefixValid)
        {
            _localSettingsService.SaveSetting("AppSettings_OSCPrefix", value);
            SetOscPrefixBackgroundColor(Application.Current!.ActualThemeVariant);
            return;
        }

        OscPrefixBackgroundColor = new SolidColorBrush(Colors.PaleVioletRed);
    }

    private void SetOscPrefixBackgroundColor(ThemeVariant theme)
    {
        // Workaround to get proper SystemChromeMediumColor color
        OscPrefixBackgroundColor = theme.ToString() switch
        {
            "Light" => new SolidColorBrush(Colors.White),
            "Dark" => SolidColorBrush.Parse("#ff202020"),
            _ => OscPrefixBackgroundColor
        };
    }

    partial void OnSteamvrAutoStartChanged(bool value)
    {
        // Ignore the initial load (the value came from disk); only apply real user toggles.
        // OpenVRService is null on non-desktop platforms where SteamVR isn't available.
        if (!_settingsLoaded || _openVrService == null)
            return;

        try
        {
            // Idempotent at the OpenVR layer; applies both enabling and disabling autostart.
            _openVrService.SteamvrAutoStart = value;
            _localSettingsService.SaveSetting("AppSettings_SteamVRAutoStart", value);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to update SteamVR AutoStart");
        }
    }

    async partial void OnUseGPUChanged(bool value)
    {
        var prev = _localSettingsService.ReadSetting("AppSettings_UseGPU", value);
        if (prev == value)
            return;

        try
        {
            _localSettingsService.SaveSetting("AppSettings_UseGPU", value);
            var loadFace = _eyePipelineManager.LoadInferenceAsync();
            var loadEye = _facePipelineManager.LoadInferenceAsync();

            await loadEye;
            await loadFace;
        }
        catch (Exception e)
        {
            _logger.LogError("", e);
        }
    }
}
