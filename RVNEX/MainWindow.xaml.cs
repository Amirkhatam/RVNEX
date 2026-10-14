using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RVNEX.Models;
using RVNEX.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace RVNEX;

public sealed class SnapshotListItem
{
    public SnapshotListItem(SnapshotRecord record)
    {
        Record = record;
        Name = record.Name;
        Summary = $"{record.VmCount:N0} VMs · {record.HostCount:N0} hosts · {record.DatastoreCount:N0} datastores";
        DateLabel = (record.CollectedUtc ?? record.ImportedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public SnapshotRecord Record { get; }
    public string Name { get; }
    public string Summary { get; }
    public string DateLabel { get; }
}

public sealed record ConnectionSelectorItem(string Id, string Label, ConnectionProfile Profile);

public sealed partial class MainWindow : Window
{
    private readonly CollectionViewModel _collectionViewModel = new();
    private readonly WorkspaceViewModel _workspaceViewModel = new();
    private readonly AppSettingsService _settings = new();
    private readonly UISettings _systemUiSettings;
    private readonly AccessibilitySettings _accessibilitySettings;
    private readonly DispatcherQueue _themeDispatcher;
    private bool _lastHighContrast;
    private readonly ConnectionProfileService _profileService = new();
    private readonly ObservableCollection<SnapshotListItem> _snapshotItems = [];
    private IReadOnlyList<ConnectionProfile> _profiles = Array.Empty<ConnectionProfile>();
    private BatchProgressPage? _batchProgressPage;
    private readonly List<BatchProgressRow> _batchRows = [];
    private readonly HashSet<string> _lastBatchFailures = new(StringComparer.Ordinal);
    private string? _selectedProfileId;
    private string? _activeBatchRunId;
    private Stopwatch? _batchStopwatch;
    private DispatcherQueueTimer? _batchTimer;
    private DateTime? _batchStartedUtc;
    private int _batchConfigured;
    private int _batchEnabled;
    private int _batchEligible;
    private CancellationTokenSource? _collectionCancellation;
    private string _currentPage = "Overview";
    private string? _baselineId;
    private string _collectionStage = "Waiting for RVTools to finish";
    private bool _suppressSelection;
    private bool _initialized;
    private int _selectionVersion;
    private SnapshotRecord? _selectedRecord;
    private WorkbookData? _selectedWorkbook;
    private System.Collections.Generic.IReadOnlyList<(SnapshotRecord Record, WorkbookData Workbook)>? _comparisonSnapshots;

    public MainWindow()
    {
        InitializeComponent();
        SplashVersion.Text = $"Version {AppProductInfo.Version}";
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RVNEX.ico");
        if (File.Exists(iconPath))
        {
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
            appWindow.SetIcon(iconPath);
        }
        string logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RVNEX-Logo.png");
        if (File.Exists(logoPath))
        {
            var logo = new BitmapImage(new Uri("ms-appx:///Assets/RVNEX-Logo.png"));
            AppBrandLogo.Source = logo;
            AppBrandLogo.Visibility = Visibility.Visible;
            SplashLogo.Source = logo;
            SplashLogo.Visibility = Visibility.Visible;
        }
        _systemUiSettings = new UISettings();
        _accessibilitySettings = new AccessibilitySettings();
        _themeDispatcher = DispatcherQueue.GetForCurrentThread();
        _lastHighContrast = _accessibilitySettings.HighContrast;
        _systemUiSettings.ColorValuesChanged += SystemUiSettings_ColorValuesChanged;
        SnapshotListView.ItemsSource = _snapshotItems;
        Activated += MainWindow_Activated;
    }

    private void SystemUiSettings_ColorValuesChanged(UISettings sender, object args)
    {
        _themeDispatcher.TryEnqueue(() =>
        {
            bool highContrast = _accessibilitySettings.HighContrast;
            if (_settings.Theme == "System" || highContrast != _lastHighContrast)
            {
                _lastHighContrast = highContrast;
                ApplyTheme();
            }
        });
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialized || e.WindowActivationState == WindowActivationState.Deactivated) return;
        _initialized = true;
        try
        {
            _settings.Load();
            ApplyTheme();
            await RefreshProfilesAsync();
            IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.LoadSnapshotsAsync();
            PopulateSnapshotList(records, records.FirstOrDefault()?.Id);
            if (!string.IsNullOrWhiteSpace(_workspaceViewModel.CatalogWarning))
            {
                SetStatus("Catalog recovery", _workspaceViewModel.CatalogWarning);
            }
            else if (records.Count == 0)
            {
                ShowEmptyWorkspace();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("Workspace unavailable", $"RVNEX could not open the local report catalog: {exception.Message}");
            ShowEmptyWorkspace();
        }
        finally
        {
            StartupSplash.Visibility = Visibility.Collapsed;
        }
    }

    private async void StartCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionCancellation is not null) return;
        ConnectionProfile? profile = _profiles.FirstOrDefault(p => p.Id == _selectedProfileId);
        if (profile is null)
        {
            await OpenConnectionManagerAsync();
            SetStatus("Choose a connection", "Add or select a saved profile in Connection Manager before collecting.");
            return;
        }
        await CollectProfileInteractivelyAsync(profile, string.Empty);
    }

    private void ConnectionProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionProfileSelector.SelectedItem is not ConnectionSelectorItem item) return;
        _selectedProfileId = item.Id;
        SelectedConnectionSummary.Text = $"{item.Profile.EndpointType}  ·  {item.Profile.Address}:{item.Profile.Port}  ·  {item.Profile.Username}  ·  {(item.Profile.Enabled ? "Enabled for batch" : "Disabled for batch")}";
    }

    private async void ConnectionsButton_Click(object sender, RoutedEventArgs e) => await OpenConnectionManagerAsync();

    private async void GetEmAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionCancellation is not null) return;
        if (_settings.ConfirmBulkCollection)
        {
            var dialog = new ContentDialog
            {
                Title = "Start Get'em All?",
                Content = $"RVNEX will collect enabled profiles sequentially. {_profiles.Count(p => p.Enabled)} profile(s) are enabled. Targets without saved credentials will be skipped without prompting.",
                PrimaryButtonText = "Start collection", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.RequestedTheme
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await RunBatchAsync(_profiles.ToArray(), Guid.NewGuid().ToString("N"));
    }

    private async Task OpenConnectionManagerAsync()
    {
        try
        {
            await RefreshProfilesAsync(_selectedProfileId);
            BatchRunRecord? latestBatch = (await _profileService.LoadBatchRunsAsync()).FirstOrDefault();
            _currentPage = "Connections";
            WorkspaceHost.Children.Clear();
            WorkspaceHost.Children.Add(new ConnectionManagerPage(
                _profiles,
                RvToolsInstallationLocator.FindExecutables(),
                latestBatch,
                profile => _profileService.HasCredentialAsync(profile),
                SaveProfileAsync,
                DeleteProfileAsync,
                DuplicateProfileAsync,
                MoveProfileAsync,
                TestProfileAsync,
                CollectProfileInteractivelyAsync,
                OpenReportPath,
                profile => { if (profile is not null) SelectProfile(profile.Id); },
                () => { _currentPage = "Overview"; ShowCurrentPage(); }));
        }
        catch (Exception ex) { SetStatus("Connection manager unavailable", ex.Message, isError: true); }
    }

    private async Task RefreshProfilesAsync(string? preferredId = null)
    {
        _profiles = await _profileService.LoadAsync();
        if (preferredId is not null && _profiles.Any(p => p.Id == preferredId)) _selectedProfileId = preferredId;
        else if (_selectedProfileId is null || !_profiles.Any(p => p.Id == _selectedProfileId)) _selectedProfileId = _profiles.FirstOrDefault()?.Id;
        ConnectionProfileSelector.ItemsSource = _profiles.Select(p => new ConnectionSelectorItem(p.Id, $"{p.Name} · {p.Address}", p)).ToArray();
        ConnectionProfileSelector.DisplayMemberPath = nameof(ConnectionSelectorItem.Label);
        ConnectionProfileSelector.SelectedItem = ConnectionProfileSelector.Items.OfType<ConnectionSelectorItem>().FirstOrDefault(i => i.Id == _selectedProfileId);
        if (ConnectionProfileSelector.SelectedItem is ConnectionSelectorItem selected)
            SelectedConnectionSummary.Text = $"{selected.Profile.EndpointType}  ·  {selected.Profile.Address}:{selected.Profile.Port}  ·  {selected.Profile.Username}  ·  {(selected.Profile.Enabled ? "Enabled for batch" : "Disabled for batch")}";
        else SelectedConnectionSummary.Text = "Open Connections to add a vCenter or ESXi profile.";
    }

    private void SelectProfile(string id)
    {
        _selectedProfileId = id;
        ConnectionProfileSelector.SelectedItem = ConnectionProfileSelector.Items.OfType<ConnectionSelectorItem>().FirstOrDefault(i => i.Id == id);
    }

    private async Task SaveProfileAsync(ConnectionProfile profile, string password, bool saveCredential)
    {
        await _profileService.SaveAsync(profile);
        if (saveCredential)
        {
            if (!string.IsNullOrEmpty(password))
            {
                await _profileService.SaveCredentialAsync(profile, password);
                profile.CredentialSaved = true;
                await _profileService.SaveAsync(profile);
            }
            else if (!profile.CredentialSaved) throw new InvalidOperationException("Enter the password before choosing to save credentials.");
        }
        else
        {
            await _profileService.DeleteCredentialAsync(profile);
            profile.CredentialSaved = false;
            await _profileService.SaveAsync(profile);
        }
        await RefreshProfilesAsync(profile.Id);
    }

    private async Task DeleteProfileAsync(string id)
    {
        await _profileService.DeleteAsync(id);
        await RefreshProfilesAsync();
    }

    private async Task<ConnectionProfile> DuplicateProfileAsync(string id)
    {
        ConnectionProfile copy = await _profileService.DuplicateAsync(id);
        await RefreshProfilesAsync(copy.Id);
        return copy;
    }

    private async Task MoveProfileAsync(string id, int direction)
    {
        await _profileService.MoveAsync(id, direction);
        await RefreshProfilesAsync(id);
    }
    private async Task CollectProfileInteractivelyAsync(ConnectionProfile profile, string suppliedPassword)
    {
        if (_collectionCancellation is not null) return;
        string? password = suppliedPassword;
        if (string.IsNullOrEmpty(password))
        {
            password = await _profileService.GetCredentialAsync(profile);
            if (password is null && profile.CredentialSaved)
            {
                await _profileService.MarkCredentialSavedAsync(profile.Id, false);
                await RefreshProfilesAsync(profile.Id);
            }
        }
        if (string.IsNullOrEmpty(password)) password = await PromptPasswordAsync(profile);
        if (password is null)
        {
            SetStatus("Collection cancelled", "No password was supplied. The profile was not changed.");
            return;
        }
        await RunSingleOperationAsync(profile, password, testOnly: false);
    }

    private async Task TestProfileAsync(ConnectionProfile profile, string suppliedPassword)
    {
        if (_collectionCancellation is not null) return;
        string? password = suppliedPassword;
        if (string.IsNullOrEmpty(password))
        {
            password = await _profileService.GetCredentialAsync(profile);
            if (password is null && profile.CredentialSaved)
            {
                await _profileService.MarkCredentialSavedAsync(profile.Id, false);
                await RefreshProfilesAsync(profile.Id);
            }
        }
        if (string.IsNullOrEmpty(password)) password = await PromptPasswordAsync(profile);
        if (password is null) return;
        await RunSingleOperationAsync(profile, password, testOnly: true);
    }

    private async Task<string?> PromptPasswordAsync(ConnectionProfile profile)
    {
        var input = new PasswordBox { Header = "Password", PlaceholderText = "Used for this operation only" };
        var dialog = new ContentDialog
        {
            Title = $"Credentials for {profile.Name}",
            Content = input,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Password : null;
    }

    private async Task RunSingleOperationAsync(ConnectionProfile profile, string password, bool testOnly)
    {
        if (_collectionCancellation is not null) return;
        _collectionCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        StartCollectionTimer(stopwatch, batch: false);
        SetCollectionBusy(true);
        _collectionStage = testOnly ? "Running authenticated RVTools test export" : "Preparing RVTools collection";
        SetStatus(testOnly ? "Testing connection" : "Starting collection", testOnly ? "Running a documented RVTools export and validating its temporary workbook..." : "Validating the saved connection profile...");
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<CollectionProgress>(update => dispatcher.TryEnqueue(() =>
        {
            _collectionStage = update.Message;
            StatusMessageText.Text = $"{update.Message} · elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}";
        }));
        try
        {
            CollectionAttemptResult result = await _collectionViewModel.StartCollectionAsync(
                profile, password, progress, _collectionCancellation.Token,
                TimeSpan.FromMinutes(_settings.CollectionTimeoutMinutes), retainReport: !testOnly);
            result = await FinalizeCollectionAsync(profile, result, batchRunId: null, testOnly, _collectionCancellation.Token);
            if (testOnly && result.IsSuccess)
            {
                await RecordAttemptAsync(profile, result, batchRunId: null, testOnly: true);
                SetStatus("Connection test succeeded", $"RVTools authenticated, completed the export, and validated a temporary XLSX. The test report was deleted. RVTools {result.RvToolsVersion}.");
            }
            else if (result.IsSuccess)
            {
                await RecordAttemptAsync(profile, result, batchRunId: null, testOnly: false);
                SetStatus("Collection complete", $"{result.Message} Snapshot added to local history.");
                StatusMessageText.Foreground = OverviewPage.ThemeBrush("SystemFillColorSuccessBrush");
            }
            else
            {
                await RecordAttemptAsync(profile, result, batchRunId: null, testOnly);
                SetStatus(GetStatusLabel(result.Status), result.Message, isError: true);
            }
        }
        catch (Exception ex)
        {
            var failed = CollectionAttemptResult.Failure(CollectionStatus.FailedExecution, "RVNEX could not finish the operation. The report was not accepted.") with { Diagnostic = $"Unexpected operation error ({ex.GetType().Name})." };
            await RecordAttemptAsync(profile, failed, null, testOnly);
            SetStatus(testOnly ? "Connection test failed" : "Collection failed", failed.Message, isError: true);
        }
        finally
        {
            stopwatch.Stop();
            StopCollectionTimer();
            _collectionCancellation.Dispose();
            _collectionCancellation = null;
            SetCollectionBusy(false);
            await RefreshProfilesAsync(profile.Id);
            if (testOnly && _currentPage == "Connections") await OpenConnectionManagerAsync();
        }
    }

    private async Task<CollectionAttemptResult> FinalizeCollectionAsync(ConnectionProfile profile, CollectionAttemptResult result, string? batchRunId, bool testOnly, CancellationToken token)
    {
        if (!result.IsSuccess) return result;
        if (testOnly) return result;
        if (string.IsNullOrWhiteSpace(result.ReportPath))
            return result with { Status = CollectionStatus.FailedWorkbookValidation, Message = "RVTools reported completion without a report path. No snapshot was registered.", Diagnostic = "Missing report path." };
        _collectionStage = "Validating workbook and saving snapshot";
        SetStatus("Verifying snapshot", "The RVTools export succeeded. RVNEX is parsing and adding the workbook to local history...");
        try
        {
            await _workspaceViewModel.RegisterCollectionAsync(result.ReportPath, profile, result, batchRunId, CancellationToken.None);
            IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.LoadSnapshotsAsync(CancellationToken.None);
            SnapshotRecord? record = records.FirstOrDefault(s => s.CollectionRunId == result.RunId);
            if (record is null) throw new InvalidDataException("The validated workbook was not found in the report catalog.");
            if (batchRunId is null) PopulateSnapshotList(records, record.Id);
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
        {
            TryDeleteOwnedReport(result.ReportPath);
            return result with { Status = CollectionStatus.FailedWorkbookValidation, Message = "The report failed RVNEX workbook parsing and was not registered as a snapshot.", ReportPath = null, Diagnostic = ex.Message };
        }
    }

    private async Task RecordAttemptAsync(ConnectionProfile profile, CollectionAttemptResult result, string? batchRunId, bool testOnly)
    {
        string status = testOnly && result.IsSuccess ? "ConnectionTestSucceeded" : result.Status.ToString();
        try { await _profileService.RecordAttemptAsync(profile.Id, new ConnectionAttempt
        {
            RunId = string.IsNullOrWhiteSpace(result.RunId) ? Guid.NewGuid().ToString("N") : result.RunId,
            BatchRunId = batchRunId,
            StartedUtc = result.StartedUtc ?? DateTime.UtcNow,
            EndedUtc = result.EndedUtc ?? DateTime.UtcNow,
            Status = status,
            Diagnostic = result.IsSuccess ? result.Diagnostic : SafeAttemptMessage(result),
            ReportPath = result.IsSuccess && !testOnly ? result.ReportPath : null,
            ExecutablePath = result.ExecutablePath,
            RvToolsVersion = result.RvToolsVersion,
            ExitCode = result.ExitCode,
            SslOverrideUsed = profile.UseSslCertificateOverride == true
        }); }
        catch (Exception ex)
        {
            SetStatus("Attempt history unavailable", $"The operation result is shown, but profile history could not be saved: {ex.Message}", isError: true);
        }
    }

    private static string SafeAttemptMessage(CollectionAttemptResult result)
    {
        string diagnostic = string.IsNullOrWhiteSpace(result.Diagnostic) ? result.Message : result.Diagnostic;
        return diagnostic.Length > 1600 ? diagnostic[^1600..] : diagnostic;
    }

    private async Task RunBatchAsync(IReadOnlyList<ConnectionProfile> targets, string batchRunId)
    {
        if (_collectionCancellation is not null) return;
        _activeBatchRunId = batchRunId;
        _collectionCancellation = new CancellationTokenSource();
        _batchStartedUtc = DateTime.UtcNow;
        _batchStopwatch = Stopwatch.StartNew();
        _batchRows.Clear(); _lastBatchFailures.Clear();
        _batchConfigured = targets.Count;
        _batchEnabled = targets.Count(p => p.Enabled);
        _batchEligible = 0;
        _currentPage = "Batch";
        _batchProgressPage = new BatchProgressPage(
            () => _ = RetryFailedTargetsAsync(),
            async id => await RetrySelectedTargetAsync(id));
        WorkspaceHost.Children.Clear(); WorkspaceHost.Children.Add(_batchProgressPage);
        SetCollectionBusy(true);
        StartCollectionTimer(_batchStopwatch, batch: true);
        SetStatus("Preparing Get'em All", "Checking enabled profiles, secure credentials, and installed RVTools versions...");

        var orchestrator = new CollectionBatchOrchestrator(
            _profileService,
            profile => _profileService.HasCredentialAsync(profile),
            profile => _profileService.GetCredentialAsync(profile),
            HasSupportedExecutable,
            (profile, password, progress, token) => _collectionViewModel.StartCollectionAsync(
                profile, password, progress, token, TimeSpan.FromMinutes(_settings.CollectionTimeoutMinutes), retainReport: true),
            (profile, result, runId, token) => FinalizeCollectionAsync(profile, result, runId, testOnly: false, token),
            _settings.MaximumRetries);

        BatchRunResult? runResult = null;
        try
        {
            runResult = await orchestrator.RunAsync(targets, batchRunId, update => ApplyBatchProgress(update), _collectionCancellation.Token);
            _batchEligible = runResult.Eligible;
            foreach (string failedId in runResult.FailedProfileIds) _lastBatchFailures.Add(failedId);
        }
        catch (Exception ex)
        {
            SetStatus("Get'em All interrupted", $"RVNEX could not finish the batch coordinator: {ex.Message}", isError: true);
        }
        finally
        {
            _batchStopwatch.Stop();
            StopCollectionTimer();
            _collectionCancellation.Dispose(); _collectionCancellation = null;
            SetCollectionBusy(false);
            try
            {
                IReadOnlyList<SnapshotRecord> snapshots = await _workspaceViewModel.LoadSnapshotsAsync();
                PopulateSnapshotList(snapshots, _selectedRecord?.Id);
                await RefreshProfilesAsync(_selectedProfileId);
            }
            catch (Exception ex) { SetStatus("History refresh failed", $"The batch finished, but local history could not refresh: {ex.Message}", isError: true); }
        }

        RefreshBatchPage();
        if (runResult is not null)
        {
            int succeeded = _batchRows.Count(r => r.Status == "Succeeded");
            int failedCount = _batchRows.Count(r => r.Status is "Failed" or "Timed out");
            int skipped = _batchRows.Count(r => r.Status == "Skipped");
            int cancelled = _batchRows.Count(r => r.Status == "Cancelled");
            string summary = $"{succeeded} succeeded · {failedCount} failed · {skipped} skipped · {cancelled} cancelled.";
            try
            {
                await _profileService.RecordBatchRunAsync(new BatchRunRecord
                {
                    RunId = runResult.RunId,
                    StartedUtc = _batchStartedUtc ?? DateTime.UtcNow,
                    EndedUtc = DateTime.UtcNow,
                    Configured = runResult.Configured,
                    Enabled = runResult.Enabled,
                    Eligible = runResult.Eligible,
                    Succeeded = succeeded,
                    Failed = failedCount,
                    Skipped = skipped,
                    Cancelled = cancelled,
                    Summary = summary
                });
            }
            catch (Exception ex) { summary += $" Batch summary could not be saved ({ex.GetType().Name})."; }
            SetStatus("Get'em All finished", $"{summary} Run {runResult.RunId}.", failedCount > 0);
        }
        _activeBatchRunId = null;
        _batchStartedUtc = null;
    }

    private void ApplyBatchProgress(BatchProgressUpdate update)
    {
        int index = _batchRows.FindIndex(row => row.ProfileId == update.Profile.Id);
        var row = new BatchProgressRow(update.Profile.Id, update.Profile.Name,
            $"{update.Profile.Address}:{update.Profile.Port}", update.State, update.Details, update.StartedUtc, update.EndedUtc);
        if (index < 0)
        {
            _batchRows.Add(row);
            if (update.State == "Queued") _batchEligible++;
        }
        else _batchRows[index] = row;
        RefreshBatchPage();
        ConnectionStatusText.Text = $"Get'em All · {update.State}";
        StatusMessageText.Text = $"{update.Profile.Name}: {update.Details}";
    }

    private bool HasSupportedExecutable(ConnectionProfile profile)
    {
        IReadOnlyList<string> installed = RvToolsInstallationLocator.FindExecutables();
        if (string.IsNullOrWhiteSpace(profile.RvToolsExecutablePath)) return installed.Count > 0;
        return installed.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(profile.RvToolsExecutablePath), StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshBatchPage()
    {
        _batchProgressPage?.Refresh(_batchRows.ToArray(), _batchConfigured, _batchEnabled, _batchEligible,
            _collectionCancellation is not null, _batchStopwatch?.Elapsed ?? TimeSpan.Zero);
    }

    private async Task RetryFailedTargetsAsync()
    {
        ConnectionProfile[] failed = _profiles.Where(p => _lastBatchFailures.Contains(p.Id)).ToArray();
        if (failed.Length == 0) { SetStatus("No failed targets", "There are no failed profiles in the last batch run."); return; }
        await RunBatchAsync(failed, Guid.NewGuid().ToString("N"));
    }

    private async Task RetrySelectedTargetAsync(string id)
    {
        ConnectionProfile? profile = _profiles.FirstOrDefault(p => p.Id == id);
        if (profile is not null) await CollectProfileInteractivelyAsync(profile, string.Empty);
    }
    private void StartCollectionTimer(Stopwatch stopwatch, bool batch)
    {
        _batchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _batchTimer.Interval = TimeSpan.FromSeconds(1);
        _batchTimer.IsRepeating = true;
        _batchTimer.Tick += (_, _) =>
        {
            if (batch) RefreshBatchPage();
            ConnectionStatusText.Text = batch ? "Get'em All running" : "RVTools collection running";
            StatusMessageText.Text = $"{_collectionStage} · elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}";
        };
        _batchTimer.Start();
    }

    private void StopCollectionTimer()
    {
        _batchTimer?.Stop();
        _batchTimer = null;
    }

    private void TryDeleteOwnedReport(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void OpenReportPath(string path)
    {
        if (!File.Exists(path)) { SetStatus("Report unavailable", "The workbook file no longer exists at its recorded path.", isError: true); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { SetStatus("Could not open workbook", ex.Message, isError: true); }
    }
    private async void ImportReportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".xlsx");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        int imported = 0;
        int duplicates = 0;
        var errors = new System.Collections.Generic.List<string>();
        SetStatus("Importing reports", $"Reading {files.Count} workbook(s) locally...");
        ImportReportButton.IsEnabled = false;
        try
        {
            string? lastImportedId = null;
            foreach (StorageFile file in files)
            {
                try
                {
                    var result = await _workspaceViewModel.ImportAsync(file.Path);
                    if (result.Duplicate)
                    {
                        duplicates++;
                        lastImportedId = result.Record?.Id ?? lastImportedId;
                    }
                    else if (result.Record is not null)
                    {
                        imported++;
                        lastImportedId = result.Record.Id;
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
                {
                    errors.Add($"{file.Name}: {exception.Message}");
                }
            }

            IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.LoadSnapshotsAsync();
            PopulateSnapshotList(records, lastImportedId ?? records.FirstOrDefault()?.Id);
            string summary = $"{imported} imported · {duplicates} duplicate(s) already in history · {errors.Count} failed.";
            if (errors.Count > 0) summary += " " + string.Join(" ", errors.Take(3));
            SetStatus(errors.Count > 0 ? "Import finished with errors" : "Import complete", summary, errors.Count > 0);
        }
        finally
        {
            ImportReportButton.IsEnabled = true;
        }
    }

    private void CancelCollectionButton_Click(object sender, RoutedEventArgs e)
    {
        _collectionCancellation?.Cancel();
        ConnectionStatusText.Text = "Cancelling collection";
        StatusMessageText.Text = "Stopping the active RVTools operation; remaining batch targets will be marked cancelled.";
    }

    private async void SnapshotListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        int version = ++_selectionVersion;
        var selected = SnapshotListView.SelectedItems.OfType<SnapshotListItem>().Select(item => item.Record).ToArray();
        HistorySelectionText.Text = selected.Length switch
        {
            0 => "Select a report to explore. Ctrl-click reports to compare.",
            1 => "One report selected · open Inventory Explorer to inspect its worksheets.",
            _ => $"{selected.Length} reports selected · comparison mode is active."
        };

        try
        {
            if (selected.Length == 0)
            {
                _selectedRecord = null;
                _selectedWorkbook = null;
                if (_currentPage == "Comparison") _currentPage = "Reports";
                ShowCurrentPage();
                return;
            }

            var loaded = new System.Collections.Generic.List<(SnapshotRecord Record, WorkbookData Workbook)>();
            foreach (SnapshotRecord record in selected)
            {
                if (!record.FileAvailable)
                {
                    throw new FileNotFoundException($"The workbook file is missing: {record.WorkbookPath}");
                }
                loaded.Add((record, await _workspaceViewModel.LoadWorkbookAsync(record)));
            }
            if (version != _selectionVersion) return;

            if (selected.Length == 1)
            {
                _selectedRecord = loaded[0].Record;
                _selectedWorkbook = loaded[0].Workbook;
                _currentPage = "Overview";
                ShowCurrentPage();
            }
            else
            {
                _selectedRecord = null;
                _selectedWorkbook = null;
                if (_baselineId is null || selected.All(record => record.Id != _baselineId))
                {
                    _baselineId = selected.OrderBy(record => record.CollectedUtc ?? record.ImportedUtc).First().Id;
                }
                _currentPage = "Comparison";
                ShowComparison(loaded);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            if (version != _selectionVersion) return;
            SetStatus("Report unavailable", exception.Message, isError: true);
        }
    }

    private async void OverviewNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = "Overview";
        await ShowCurrentPageAsync();
    }

    private async void InventoryNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = "Inventory";
        await ShowCurrentPageAsync();
    }

    private async void ReportsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = "Reports";
        await ShowCurrentPageAsync();
    }

    private void SettingsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        _currentPage = "Settings";
        ShowCurrentPage();
    }

    private void ClearComparisonButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotListView.SelectedItems.Count == 0) return;
        _suppressSelection = true;
        object keep = SnapshotListView.SelectedItems[0];
        SnapshotListView.SelectedItems.Clear();
        SnapshotListView.SelectedItems.Add(keep);
        _suppressSelection = false;
        _selectionVersion++;
        if (keep is SnapshotListItem item)
        {
            _ = SelectOneAsync(item.Record);
        }
    }

    private async Task SelectOneAsync(SnapshotRecord record)
    {
        try
        {
            _selectedRecord = record;
            _selectedWorkbook = await _workspaceViewModel.LoadWorkbookAsync(record);
            _currentPage = "Overview";
            ShowCurrentPage();
            HistorySelectionText.Text = "One report selected · open Inventory Explorer to inspect its worksheets.";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus("Report unavailable", exception.Message, isError: true);
        }
    }

    private async Task ShowCurrentPageAsync()
    {
        if (_currentPage == "Inventory" && _selectedRecord is not null && _selectedWorkbook is null)
        {
            _selectedWorkbook = await _workspaceViewModel.LoadWorkbookAsync(_selectedRecord);
        }
        ShowCurrentPage();
    }

    private void ShowCurrentPage()
    {
        UpdateNavigationAppearance();
        if (SnapshotListView.SelectedItems.Count > 1)
        {
            return;
        }

        WorkspaceHost.Children.Clear();
        if (_currentPage == "Settings")
        {
            WorkspaceHost.Children.Add(new SettingsPage(
                _settings.CollectionTimeoutMinutes,
                _settings.Theme,
                minutes =>
                {
                    _settings.SaveCollectionTimeout(minutes);
                    SetStatus("Settings saved", $"RVTools collection timeout is now {minutes} minutes.");
                },
                theme =>
                {
                    _settings.SaveTheme(theme);
                    ApplyTheme();
                    SetStatus("Settings saved", $"RVNEX theme is now {theme}.");
                },
                _settings.ConfirmBulkCollection,
                confirm =>
                {
                    _settings.SaveConfirmBulkCollection(confirm);
                    SetStatus("Settings saved", confirm ? "Get'em All will show a separate confirmation." : "Get'em All will start with one click.");
                },
                _settings.MaximumRetries,
                retries =>
                {
                    _settings.SaveMaximumRetries(retries);
                    SetStatus("Settings saved", $"Get'em All will retry transient connection failures up to {retries} time(s).");
                }));
            return;
        }
        if (_currentPage == "Reports")
        {
            SnapshotRecord? record = _selectedRecord;
            WorkspaceHost.Children.Add(new ReportsPage(record,
                () => _ = RefreshCatalogAsync(),
                () => OpenWorkbook(record),
                () => _ = RenameRecordAsync(record),
                () => _ = RemoveRecordAsync(record)));
            return;
        }
        if (_selectedRecord is null || _selectedWorkbook is null)
        {
            ShowEmptyWorkspace();
            return;
        }
        if (_currentPage == "Inventory")
        {
            WorkspaceHost.Children.Add(new InventoryPage(_selectedWorkbook, _workspaceViewModel.Analytics));
        }
        else
        {
            WorkspaceHost.Children.Add(new OverviewPage(_selectedRecord, _selectedWorkbook, _workspaceViewModel.Analytics));
        }
    }

    private void ShowComparison(System.Collections.Generic.IReadOnlyList<(SnapshotRecord Record, WorkbookData Workbook)> snapshots)
    {
        UpdateNavigationAppearance();
        _comparisonSnapshots = snapshots;
        WorkspaceHost.Children.Clear();
        WorkspaceHost.Children.Add(new ComparisonPage(snapshots, _workspaceViewModel.Analytics, _baselineId, id =>
        {
            _baselineId = id;
            if (_comparisonSnapshots is not null)
            {
                ShowComparison(_comparisonSnapshots);
            }
        }));
    }

    private void ShowEmptyWorkspace()
    {
        UpdateNavigationAppearance();
        WorkspaceHost.Children.Clear();
        var empty = new StackPanel { Spacing = 10, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        empty.Children.Add(new TextBlock { Text = "Your offline inventory workspace", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        empty.Children.Add(new TextBlock { Text = "Import one or more RVTools .xlsx reports to explore saved infrastructure inventory without a live vCenter connection. You can also collect a new report above when RVTools is available.", FontSize = 15, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        var import = new Button { Content = "Import existing reports", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        import.Click += ImportReportButton_Click;
        empty.Children.Add(import);
        WorkspaceHost.Children.Add(empty);
    }

    private void PopulateSnapshotList(System.Collections.Generic.IReadOnlyList<SnapshotRecord> records, string? selectedId)
    {
        _suppressSelection = true;
        _snapshotItems.Clear();
        foreach (SnapshotRecord record in records)
        {
            _snapshotItems.Add(new SnapshotListItem(record));
        }
        SnapshotListView.SelectedItems.Clear();
        SnapshotListItem? selected = _snapshotItems.FirstOrDefault(item => item.Record.Id == selectedId);
        if (selected is not null)
        {
            SnapshotListView.SelectedItems.Add(selected);
        }
        _suppressSelection = false;

        if (_currentPage == "Batch") return;

        if (selected is not null)
        {
            _ = SelectOneAsync(selected.Record);
        }
        else
        {
            _selectedRecord = null;
            _selectedWorkbook = null;
            ShowEmptyWorkspace();
        }
    }

    private async Task RenameRecordAsync(SnapshotRecord? record)
    {
        if (record is null) return;
        var input = new TextBox { Text = record.Name, PlaceholderText = "Report name" };
        var dialog = new ContentDialog
        {
            Title = "Rename report",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.RenameAsync(record.Id, input.Text);
        PopulateSnapshotList(records, record.Id);
        _currentPage = "Reports";
        ShowCurrentPage();
    }

    private async Task RemoveRecordAsync(SnapshotRecord? record)
    {
        if (record is null) return;
        IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.RemoveAsync(record.Id);
        string? selectId = records.FirstOrDefault()?.Id;
        PopulateSnapshotList(records, selectId);
        _currentPage = "Reports";
        ShowCurrentPage();
        SetStatus("Catalog updated", "The report was removed from history. Its workbook was left in place.");
    }

    private async Task RefreshCatalogAsync()
    {
        try
        {
            IReadOnlyList<SnapshotRecord> records = await _workspaceViewModel.RefreshAsync();
            string? selectedId = _selectedRecord?.Id ?? records.FirstOrDefault()?.Id;
            PopulateSnapshotList(records, selectedId);
            _currentPage = "Reports";
            ShowCurrentPage();
            SetStatus("Catalog refreshed", "File availability and report history have been refreshed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus("Refresh failed", exception.Message, isError: true);
        }
    }

    private void OpenWorkbook(SnapshotRecord? record)
    {
        if (record is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(record.WorkbookPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus("Could not open workbook", exception.Message, isError: true);
        }
    }

    private void SetCollectionBusy(bool busy)
    {
        StartCollectionButton.IsEnabled = !busy;
        ConnectionsButton.IsEnabled = !busy;
        ConnectionProfileSelector.IsEnabled = !busy;
        GetEmAllButton.IsEnabled = !busy;
        ImportReportButton.IsEnabled = !busy;
        CancelCollectionButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressIndicator.IsActive = busy;
        ProgressIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CollectionProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTheme()
    {
        bool isDark = ThemePalette.ResolveIsDark(_settings.Theme, _systemUiSettings);
        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        ThemePalette.Apply(Application.Current.Resources, isDark, _systemUiSettings, _accessibilitySettings.HighContrast);
    }

    private void UpdateNavigationAppearance()
    {
        ResourceDictionary resources = Application.Current.Resources;
        Brush idleBackground = (Brush)resources["RvnexNavigationIdleBrush"];
        Brush selectedBackground = (Brush)resources["RvnexSelectionBrush"];
        Brush selectedForeground = (Brush)resources["RvnexAccentBrush"];
        Brush normalForeground = (Brush)resources["RvnexTextPrimaryBrush"];
        Brush selectedBorder = (Brush)resources["RvnexAccentBrush"];

        void SetState(Button button, bool selected)
        {
            button.Background = selected ? selectedBackground : idleBackground;
            button.Foreground = selected ? selectedForeground : normalForeground;
            button.BorderBrush = selected ? selectedBorder : idleBackground;
            button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        }

        SetState(OverviewNavigationButton, _currentPage == "Overview");
        SetState(InventoryNavigationButton, _currentPage == "Inventory");
        SetState(ReportsNavigationButton, _currentPage is "Reports" or "Comparison");
        SetState(SettingsNavigationButton, _currentPage == "Settings");
    }

    private void SetStatus(string status, string message, bool isError = false)
    {
        ConnectionStatusText.Text = status;
        StatusMessageText.Text = message;
        StatusMessageText.Foreground = OverviewPage.ThemeBrush(isError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }

    private static string GetStatusLabel(CollectionStatus status) => status switch
    {
        CollectionStatus.ValidationError => "Action required",
        CollectionStatus.MissingRvTools => "RVTools not found",
        CollectionStatus.FailedAuthentication => "Authentication failed",
        CollectionStatus.FailedConnection => "Connection failed",
        CollectionStatus.FailedTlsOrCertificate => "TLS or certificate validation failed",
        CollectionStatus.UnresolvedTlsFailure => "TLS failure unresolved",
        CollectionStatus.TimedOut => "Collection timed out",
        CollectionStatus.FailedWorkbookValidation => "Report verification failed",
        CollectionStatus.Cancelled => "Collection cancelled",
        CollectionStatus.SkippedMissingCredential => "Skipped: missing credential",
        CollectionStatus.SkippedDisabled => "Skipped: disabled",
        CollectionStatus.UnsupportedPort => "Unsupported server port",
        CollectionStatus.Succeeded => "Collection complete",
        _ => "Collection failed"
    };
}
