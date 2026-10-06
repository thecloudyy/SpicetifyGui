using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketplaceInstaller.Gui.Services;
using Wpf.Ui.Controls;

namespace MarketplaceInstaller.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x3f, 0xb9, 0x50));
    private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x4c, 0xc2, 0xff));
    private static readonly Brush TodoBrush = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xf1, 0x4c, 0x4c));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x85, 0x93));

    private static readonly string[] WatchedFiles =
        { "manifest.json", "index.js", "extension.js", "style.css" };

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _selfUpdateCts;
    private readonly SelfUpdater _selfUpdater = new();
    private bool _spicetifyOk;
    private string _stepMode = "install";

    // Fixed location, auto-detected only — never shown or browsed.
    private static string Dir => MarketplacePaths.Marketplace;

    [ObservableProperty] private string _systemStatus = "Checking…";
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _statusKnown;
    [ObservableProperty] private string _installedTag = "";
    [ObservableProperty] private string _statusMessage = "Checking…";
    [ObservableProperty] private SymbolRegular _statusIcon = SymbolRegular.Info24;
    [ObservableProperty] private Brush _statusIconColor = new SolidColorBrush(Color.FromRgb(0x7c, 0x85, 0x93));
    [ObservableProperty] private string _currentStep = "";
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private int _stepIndex = -1;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSelfUpdating;
    [ObservableProperty] private double _selfUpdateProgress;
    [ObservableProperty] private string _selfUpdateStatus = "";
    [ObservableProperty] private bool _canSelfUpdate = true;

    public ObservableCollection<StepItem> Steps { get; } = new();
    public ObservableCollection<FileStatusItem> FileRows { get; } = new();

    public string InstalledLabel
    {
        get
        {
            if (!StatusKnown) return "Checking…";
            if (!IsInstalled) return "Not installed";
            return InstalledTag == "" ? "Installed" : InstalledTag;
        }
    }
    public string AppVersion => "v" + MarketplacePaths.Version;
    public string InstallUninstallText => IsInstalled ? "Uninstall" : "Install";
    public string FilesPillText => InstalledTag == "" ? "Installed" : InstalledTag;

    private bool CanRun() => !IsBusy;

    partial void OnInstalledTagChanged(string value)
    {
        OnPropertyChanged(nameof(InstalledLabel));
        OnPropertyChanged(nameof(FilesPillText));
    }
    partial void OnStatusKnownChanged(bool value) => OnPropertyChanged(nameof(InstalledLabel));
    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(InstalledLabel));
        OnPropertyChanged(nameof(InstallUninstallText));
        OnPropertyChanged(nameof(FilesPillText));
    }
    partial void OnIsBusyChanged(bool value)
    {
        InstallUninstallCommand.NotifyCanExecuteChanged();
        UpdateModCommand.NotifyCanExecuteChanged();
        RefreshStatusIcon();
    }
    partial void OnStatusMessageChanged(string value) => RefreshStatusIcon();

    private void RefreshStatusIcon()
    {
        if (IsBusy || StatusMessage.EndsWith("…"))
        {
            StatusIcon = SymbolRegular.ArrowClockwise24;
            StatusIconColor = ActiveBrush;
            return;
        }
        string t = StatusMessage;
        if (t.Contains("Failed") || t.Contains("not found") || t.Contains("Check failed"))
        {
            StatusIcon = SymbolRegular.ErrorCircle24;
            StatusIconColor = ErrorBrush;
        }
        else if (t.Contains("Cancell"))
        {
            StatusIcon = SymbolRegular.Warning24;
            StatusIconColor = WarnBrush;
        }
        else if (t.Contains("Ready") || t.Contains("Done") || t.Contains("up to date")
            || t.Contains("Updated") || t.Contains("Installed") || t.Contains("Uninstall done")
            || t.Contains("Restarted") || t.Contains("Complete") || t.Contains("Already"))
        {
            StatusIcon = SymbolRegular.CheckmarkCircle24;
            StatusIconColor = OkBrush;
        }
        else
        {
            StatusIcon = SymbolRegular.Info24;
            StatusIconColor = DimBrush;
        }
    }
    partial void OnStepIndexChanged(int value) => RefreshSteps();

    public async Task InitializeAsync()
    {
        ResetSteps("install");
        await AutoDetectAsync();
    }

    private void ResetSteps(string mode)
    {
        _stepMode = mode;
        Steps.Clear();
        string[] labels = mode == "remove"
            ? new[] { "Prepare", "Remove", "Apply", "Done" }
            : new[] { "Prepare", "Download", "Install", "Apply" };
        foreach (string l in labels)
            Steps.Add(new StepItem { Label = l, Bar = TodoBrush });
        StepIndex = -1;
        StepText = "";
        ProgressText = "";
        CurrentStep = "";
    }

    private void RefreshSteps()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (StepIndex >= Steps.Count || i < StepIndex)
                Steps[i].Bar = DoneBrush;
            else if (i == StepIndex)
                Steps[i].Bar = ActiveBrush;
            else
                Steps[i].Bar = TodoBrush;
        }
        StepText = StepIndex < 0 ? ""
            : StepIndex >= Steps.Count ? "Complete"
            : $"Step {StepIndex + 1} of {Steps.Count}";
    }

    private void TrackStep(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        CurrentStep = line.Trim();
        string t = CurrentStep.ToLowerInvariant();
        int s;
        if (_stepMode == "remove")
        {
            if (t.Contains("apply") || t.Contains("left closed") || t.Contains("uninstall done")) s = 2;
            else if (t.Contains("uninstall") || t.Contains("backup") || t.Contains("moved install")) s = 1;
            else s = 0;
        }
        else
        {
            if (t.Contains("apply") || t.Contains("register") || t.Contains("almost done")) s = 3;
            else if (t.Contains("installing") || t.Contains("extract")) s = 2;
            else if (t.Contains("download")) s = 1;
            else s = 0;
        }
        if (s > StepIndex) StepIndex = s;
    }

    private async Task AutoDetectAsync()
    {
        if (IsBusy) return;
        SystemStatus = "Checking…";
        try
        {
            var (ok, version, present, local, latest) = await Task.Run(async () =>
            {
                string? lt = null;
                try { lt = await MarketplaceOps.LatestReleaseVersionAsync(CancellationToken.None); }
                catch { }
                return (MarketplaceOps.IsSpicetifyInstalled(),
                    MarketplaceOps.SpicetifyVersion(),
                    MarketplaceOps.IsPresent(Dir),
                    MarketplaceOps.LocalVersion(Dir),
                    lt);
            });
            _spicetifyOk = ok;
            IsInstalled = present;
            InstalledTag = local != null ? "v" + local : "";
            StatusKnown = true;
            RefreshFileRows(present, local, latest);

            if (!ok)
            {
                SystemStatus = "Spicetify missing";
                StatusMessage = "Spicetify not found.";
            }
            else if (IsInstalled)
            {
                SystemStatus = InstalledTag == ""
                    ? $"Spicetify {version} · installed"
                    : $"Spicetify {version} · {InstalledTag}";
                StatusMessage = "Ready.";
            }
            else
            {
                SystemStatus = $"Spicetify {version} · not installed";
                StatusMessage = "Ready.";
            }
        }
        catch
        {
            SystemStatus = "Check failed.";
            StatusMessage = "Check failed.";
        }
    }

    private void RefreshFileRows(bool present, string? local, string? latest)
    {
        FileRows.Clear();
        int found = 0;
        foreach (string f in WatchedFiles)
        {
            bool ok = present && File.Exists(Path.Combine(Dir, f));
            if (ok) found++;
            FileRows.Add(new FileStatusItem
            {
                Name = f,
                Detail = ok ? "Installed" : "Missing",
                Icon = ok ? Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24
                          : Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
                IconColor = ok ? OkBrush : DimBrush,
            });
        }

        var release = new FileStatusItem { Name = "Release" };
        if (!present)
        {
            release.Detail = "Not installed";
            release.Icon = Wpf.Ui.Controls.SymbolRegular.DismissCircle24;
            release.IconColor = DimBrush;
        }
        else if (local == null)
        {
            release.Detail = latest != null ? $"v{latest} available" : "Unknown";
            release.Icon = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
            release.IconColor = WarnBrush;
        }
        else if (latest == null)
        {
            release.Detail = $"v{local}";
            release.Icon = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
            release.IconColor = DimBrush;
        }
        else if (local == latest)
        {
            release.Detail = $"v{local} · up to date";
            release.Icon = Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24;
            release.IconColor = OkBrush;
        }
        else
        {
            release.Detail = $"v{local} → v{latest}";
            release.Icon = Wpf.Ui.Controls.SymbolRegular.ArrowDownload24;
            release.IconColor = WarnBrush;
        }
        FileRows.Add(release);
    }

    private bool RequireEnv()
    {
        _spicetifyOk = MarketplaceOps.IsSpicetifyInstalled();
        if (_spicetifyOk) return true;
        StatusMessage = "Spicetify not found.";
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallUninstallAsync()
    {
        if (IsBusy) return;
        if (IsInstalled)
        {
            ResetSteps("remove");
            await RunOpAsync((log, _, ct) =>
                Task.FromResult(MarketplaceOps.UninstallFlow(Dir, log, ct)),
                "Uninstalling…");
        }
        else
        {
            if (!RequireEnv()) return;
            ResetSteps("install");
            await RunOpAsync((log, bar, ct) =>
                MarketplaceOps.InstallFlowAsync(Dir, log, bar, ct),
                "Installing…");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateModAsync()
    {
        if (IsBusy) return;
        if (!RequireEnv()) return;
        ResetSteps("install");
        await RunOpAsync((log, bar, ct) =>
            MarketplaceOps.UpdateFlowAsync(Dir, log, bar, ct),
            "Updating…");
    }

    [RelayCommand]
    private void CancelRun()
    {
        try { _runCts?.Cancel(); } catch { }
        try { _selfUpdateCts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private async Task ReinstallAppAsync()
    {
        if (IsSelfUpdating || IsBusy) return;
        IsSelfUpdating = true;
        CanSelfUpdate = false;
        SelfUpdateProgress = 0;
        SelfUpdateStatus = "";
        _selfUpdateCts = new CancellationTokenSource();
        try
        {
            var info = await _selfUpdater.GetLatestWithAssetAsync(_selfUpdateCts.Token);
            if (info == null)
            {
                SelfUpdateStatus = "No setup file published yet.";
                return;
            }
            SelfUpdateStatus = $"Downloading {info.FileName}…";
            var prog = new Progress<double>(v => SelfUpdateProgress = v * 100);
            string dir = await _selfUpdater.DownloadAsync(info, prog, _selfUpdateCts.Token);
            SelfUpdateStatus = "Restarting to apply…";
            SelfUpdater.InstallAndRestart(dir, info.FileName);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            SelfUpdateStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            SelfUpdateStatus = "Reinstall failed: " + ex.Message;
        }
        finally
        {
            IsSelfUpdating = false;
            CanSelfUpdate = true;
        }
    }

    private static readonly string[] SummaryPrefixes =
    {
        "OK:", "Installed ", "Updated to ", "Already up to date",
        "Uninstall done", "Download verified.", "Update complete."
    };

    private static string FriendlyStatus(List<string> lines, int code)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string t = lines[i].Trim();
            if (t == "") continue;
            foreach (string p in SummaryPrefixes)
            {
                if (t.StartsWith(p, StringComparison.Ordinal))
                    return t;
            }
        }
        return code == 0 ? "Done."
            : code == 4 ? "Already up to date."
            : "Failed.";
    }

    private async Task RunOpAsync(Func<IProgress<string>, IProgress<double>, CancellationToken, Task<int>> op, string phase)
    {
        if (IsBusy) return;
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        IsProgressIndeterminate = true;
        Progress = 0;
        ProgressText = "";
        StatusMessage = phase;
        StepIndex = 0;
        var lines = new List<string>();
        var log = new Progress<string>(line => { lines.Add(line); TrackStep(line); });
        var bar = new Progress<double>(v =>
        {
            if (v < 0) { IsProgressIndeterminate = true; ProgressText = ""; }
            else { IsProgressIndeterminate = false; Progress = v / 100.0; ProgressText = $"{v:0}%"; }
        });
        try
        {
            int code = await op(log, bar, _runCts.Token);
            StatusMessage = code == 4 ? "Already up to date." : FriendlyStatus(lines, code);
            if (code is 0 or 4) { StepIndex = Steps.Count; CurrentStep = ""; ProgressText = ""; }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch
        {
            StatusMessage = "Failed.";
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
        await AutoDetectAsync();
    }
}
