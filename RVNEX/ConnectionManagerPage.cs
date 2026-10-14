using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RVNEX.Models;
using RVNEX.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace RVNEX;

internal sealed class ConnectionManagerPage : UserControl
{
    private readonly List<ConnectionProfile> _profiles;
    private readonly IReadOnlyList<string> _executables;
    private readonly BatchRunRecord? _mostRecentBatch;
    private readonly Func<ConnectionProfile, Task<bool>> _hasCredential;
    private readonly Func<ConnectionProfile, string, bool, Task> _save;
    private readonly Func<string, Task> _delete;
    private readonly Func<string, Task<ConnectionProfile>> _duplicate;
    private readonly Func<string, int, Task> _move;
    private readonly Func<ConnectionProfile, string, Task> _test;
    private readonly Func<ConnectionProfile, string, Task> _collect;
    private readonly Action<string> _openReport;
    private readonly Action<ConnectionProfile?> _select;
    private readonly Action _close;
    private readonly TextBlock _metrics = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { PlaceholderText = "Search name, endpoint, username, or notes" };
    private readonly ComboBox _groupFilter = new() { Width = 170, PlaceholderText = "All groups" };
    private readonly ComboBox _typeFilter = new() { Width = 150, PlaceholderText = "All endpoint types" };
    private readonly ComboBox _statusFilter = new() { Width = 180, PlaceholderText = "All health states" };
    private readonly ComboBox _timeFilter = new() { Width = 140, PlaceholderText = "Any time" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, BorderThickness = new Thickness(0) };
    private readonly TextBox _name = new() { Header = "Profile name", PlaceholderText = "Production vCenter" };
    private readonly ComboBox _endpointType = new() { Header = "Endpoint type", Width = 220, ItemsSource = new[] { "vCenter Server", "Standalone ESXi host" }, SelectedIndex = 0 };
    private readonly TextBox _address = new() { Header = "Address", PlaceholderText = "vcsa.example.com" };
    private readonly TextBox _port = new() { Header = "Port", Text = "443", Width = 110 };
    private readonly TextBox _username = new() { Header = "Username", PlaceholderText = "administrator@vsphere.local" };
    private readonly TextBox _group = new() { Header = "Environment group", PlaceholderText = "Production, DR, Lab..." };
    private readonly TextBox _notes = new() { Header = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 74 };
    private readonly ComboBox _executable = new() { Header = "RVTools installation", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _enabled = new() { Content = "Include this profile in Get'em All", IsChecked = true };
    private readonly CheckBox _allowSslOverride = new() { Content = "Allow RVTools to bypass SSL certificate validation (less secure)" };
    private readonly CheckBox _saveCredential = new() { Content = "Save credentials securely in Windows Credential Manager" };
    private readonly PasswordBox _password = new() { Header = "Password (used only when saving or testing)", PlaceholderText = "Leave empty to keep the saved password" };
    private readonly TextBlock _credentialStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _resultStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ListView _history = new() { MaxHeight = 180, SelectionMode = ListViewSelectionMode.None };
    private ConnectionProfile? _selected;
    private bool _loading;
    private bool _hasSavedCredential;
    private readonly HashSet<string> _credentialIds = new(StringComparer.Ordinal);

    public ConnectionManagerPage(
        IReadOnlyList<ConnectionProfile> profiles,
        IReadOnlyList<string> executables,
        BatchRunRecord? mostRecentBatch,
        Func<ConnectionProfile, Task<bool>> hasCredential,
        Func<ConnectionProfile, string, bool, Task> save,
        Func<string, Task> delete,
        Func<string, Task<ConnectionProfile>> duplicate,
        Func<string, int, Task> move,
        Func<ConnectionProfile, string, Task> test,
        Func<ConnectionProfile, string, Task> collect,
        Action<string> openReport,
        Action<ConnectionProfile?> select,
        Action close)
    {
        _profiles = profiles.ToList();
        _executables = executables;
        _mostRecentBatch = mostRecentBatch;
        _hasCredential = hasCredential;
        _save = save;
        _delete = delete;
        _duplicate = duplicate;
        _move = move;
        _test = test;
        _collect = collect;
        _openReport = openReport;
        _select = select;
        _close = close;
        Content = Build();
        _ = InitializeAsync();
    }

    private UIElement Build()
    {
        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(OverviewPage.PageHeader("Connection Manager", "Maintain independent vCenter and ESXi profiles, securely store credentials, and review collection health."));
        root.Children.Add(_metrics);

        var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _search.Width = 320;
        _groupFilter.Items.Add("All groups");
        foreach (string group in _profiles.Select(p => p.Group).OfType<string>().Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g)) _groupFilter.Items.Add(group);
        _groupFilter.SelectedIndex = 0;
        _typeFilter.Items.Add("All endpoint types"); _typeFilter.Items.Add("vCenter Server"); _typeFilter.Items.Add("Standalone ESXi host"); _typeFilter.SelectedIndex = 0;
        _statusFilter.Items.Add("All health states");
        foreach (string state in new[] { "Last collection succeeded", "Last collection failed", "Connection test succeeded", "Connection never tested", "Missing credentials", "Disabled", "Missing or unsupported RVTools" }) _statusFilter.Items.Add(state);
        _statusFilter.SelectedIndex = 0;
        _timeFilter.Items.Add("Any time"); _timeFilter.Items.Add("24 hours"); _timeFilter.Items.Add("7 days"); _timeFilter.Items.Add("30 days"); _timeFilter.SelectedIndex = 0;
        _search.TextChanged += (_, _) => RenderProfiles();
        _groupFilter.SelectionChanged += (_, _) => RenderProfiles();
        _typeFilter.SelectionChanged += (_, _) => RenderProfiles();
        _statusFilter.SelectionChanged += (_, _) => RenderProfiles();
        _timeFilter.SelectionChanged += (_, _) => RenderProfiles();
        filters.Children.Add(_search); filters.Children.Add(_groupFilter); filters.Children.Add(_typeFilter); filters.Children.Add(_statusFilter); filters.Children.Add(_timeFilter);
        root.Children.Add(filters);

        var body = new Grid { ColumnSpacing = 18, MinHeight = 570 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = OverviewPage.Surface();
        var listPanel = new StackPanel { Spacing = 8 };
        var listActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var add = new Button { Content = "New" }; add.Click += (_, _) => NewProfile();
        var duplicate = new Button { Content = "Duplicate" }; duplicate.Click += async (_, _) => await DuplicateSelectedAsync();
        var delete = new Button { Content = "Delete", Style = (Style)Application.Current.Resources["RvnexDestructiveButtonStyle"] }; delete.Click += async (_, _) => await DeleteSelectedAsync();
        listActions.Children.Add(add); listActions.Children.Add(duplicate); listActions.Children.Add(delete);
        listPanel.Children.Add(listActions);
        _list.MinHeight = 430;
        _list.SelectionChanged += (_, _) => SelectProfile((_list.SelectedItem as ListViewItem)?.Tag as ConnectionProfile);
        listPanel.Children.Add(_list);
        var ordering = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var moveUp = new Button { Content = "Move up" }; moveUp.Click += async (_, _) => await MoveSelectedAsync(-1);
        var moveDown = new Button { Content = "Move down" }; moveDown.Click += async (_, _) => await MoveSelectedAsync(1);
        ordering.Children.Add(moveUp); ordering.Children.Add(moveDown); listPanel.Children.Add(ordering);
        left.Child = listPanel;
        Grid.SetColumn(left, 0); body.Children.Add(left);

        var right = OverviewPage.Surface();
        var form = new StackPanel { Spacing = 9 };
        var row1 = new Grid { ColumnSpacing = 10 };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.Children.Add(_name); Grid.SetColumn(_endpointType, 1); row1.Children.Add(_endpointType); form.Children.Add(row1);
        var row2 = new Grid { ColumnSpacing = 10 };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row2.Children.Add(_address); Grid.SetColumn(_port, 1); row2.Children.Add(_port); form.Children.Add(row2);
        form.Children.Add(_username); form.Children.Add(_group); form.Children.Add(_notes);
        _executable.Items.Add(new ComboBoxItem { Content = "Auto-detect installed RVTools", Tag = string.Empty });
        foreach (string path in _executables)
        {
            string version = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "Unknown version";
            _executable.Items.Add(new ComboBoxItem { Content = $"RVTools {version}  ·  {path}", Tag = path });
        }
        _executable.SelectedIndex = 0;
        form.Children.Add(_executable);
        form.Children.Add(_enabled);
        form.Children.Add(_allowSslOverride);
        form.Children.Add(new TextBlock
        {
            Text = "RVTools 4.8.1+ validates server certificates by default. Keep this off when the certificate is trusted. Enable it only when you have independently verified and trust a server whose certificate cannot be validated; RVNEX will then pass -BypassSSLCheck.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush")
        });
        form.Children.Add(_credentialStatus);
        form.Children.Add(_saveCredential);
        form.Children.Add(_password);
        form.Children.Add(new TextBlock
        {
            Text = "RVNEX uses RVTools 4.5+ DPAPI-protected command-line passwords. Saved credentials remain in Windows Credential Manager, and the plaintext password is not passed to RVTools or written to history.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush")
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var save = new Button { Content = "Save profile", Style = (Style)Application.Current.Resources["RvnexPrimaryButtonStyle"] }; save.Click += async (_, _) => await SaveSelectedAsync();
        var test = new Button { Content = "Test connection" }; test.Click += async (_, _) => await TestSelectedAsync();
        var collect = new Button { Content = "Collect this profile" }; collect.Click += async (_, _) => await CollectSelectedAsync();
        var close = new Button { Content = "Back to workspace" }; close.Click += (_, _) => _close();
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(collect); buttons.Children.Add(close);
        form.Children.Add(buttons);
        _resultStatus.Margin = new Thickness(0, 4, 0, 0); form.Children.Add(_resultStatus);
        form.Children.Add(new TextBlock { Text = "Recent attempts", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        form.Children.Add(_history);
        right.Child = form; Grid.SetColumn(right, 1); body.Children.Add(right);
        root.Children.Add(body);
        return root;
    }

    private async Task InitializeAsync()
    {
        foreach (ConnectionProfile profile in _profiles)
        {
            try { profile.CredentialTarget = string.IsNullOrWhiteSpace(profile.CredentialTarget) ? CredentialVault.GetTarget(profile.Id) : profile.CredentialTarget; }
            catch { }
        }
        foreach (ConnectionProfile profile in _profiles)
        {
            try { if (await _hasCredential(profile)) _credentialIds.Add(profile.Id); }
            catch { }
        }
        RenderProfiles();
        UpdateMetrics();
        if (_profiles.Count > 0) _list.SelectedItem = _profiles.OrderBy(p => p.SortOrder).First();
        else NewProfile();
        await Task.CompletedTask;
    }

    private void RenderProfiles()
    {
        if (_loading) return;
        string query = _search.Text.Trim();
        string group = _groupFilter.SelectedItem as string ?? "All groups";
        string type = _typeFilter.SelectedItem as string ?? "All endpoint types";
        string status = _statusFilter.SelectedItem as string ?? "All health states";
        string time = _timeFilter.SelectedItem as string ?? "Any time";
        ConnectionProfile[] filtered = _profiles.Where(p =>
        {
            bool matchesText = string.IsNullOrWhiteSpace(query) || $"{p.Name} {p.Address} {p.Username} {p.Group} {p.Notes}".Contains(query, StringComparison.OrdinalIgnoreCase);
            bool matchesGroup = group == "All groups" || string.Equals(group, p.Group, StringComparison.OrdinalIgnoreCase);
            bool matchesType = type == "All endpoint types" || (type == "vCenter Server" ? p.EndpointType == EndpointType.VCenterServer : p.EndpointType == EndpointType.StandaloneEsxi);
            bool matchesStatus = status == "All health states" || Health(p) == status;
            TimeSpan? range = time switch { "24 hours" => TimeSpan.FromHours(24), "7 days" => TimeSpan.FromDays(7), "30 days" => TimeSpan.FromDays(30), _ => null };
            bool matchesTime = range is null || p.LastAttemptUtc >= DateTime.UtcNow - range.Value;
            return matchesText && matchesGroup && matchesType && matchesStatus && matchesTime;
        }).OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        _list.Items.Clear();
        foreach (ConnectionProfile p in filtered)
        {
            var row = new StackPanel { Spacing = 3, Padding = new Thickness(4, 5, 4, 5) };
            row.Children.Add(new TextBlock { Text = p.Name + (p.Enabled ? string.Empty : "  ·  Disabled"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = $"{p.Address}:{p.Port}  ·  {p.Group ?? "No group"}", FontSize = 11, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = Health(p), FontSize = 10, Foreground = OverviewPage.ThemeBrush(Health(p).Contains("failed", StringComparison.OrdinalIgnoreCase) ? "SystemFillColorCriticalBrush" : "TextFillColorTertiaryBrush") });
            var item = new ListViewItem { Content = row, Tag = p };
            _list.Items.Add(item);
        }
        if (_selected is not null)
            _list.SelectedItem = _list.Items.OfType<ListViewItem>().FirstOrDefault(i => (i.Tag as ConnectionProfile)?.Id == _selected.Id);
    }

    private void SelectProfile(ConnectionProfile? profile)
    {
        if (profile is null) return;
        _selected = profile;
        _loading = true;
        _name.Text = profile.Name; _endpointType.SelectedIndex = profile.EndpointType == EndpointType.VCenterServer ? 0 : 1;
        _address.Text = profile.Address; _port.Text = profile.Port.ToString(); _username.Text = profile.Username;
        _group.Text = profile.Group ?? string.Empty; _notes.Text = profile.Notes ?? string.Empty; _enabled.IsChecked = profile.Enabled;
        _allowSslOverride.IsChecked = profile.UseSslCertificateOverride == true;
        string selectedExe = profile.RvToolsExecutablePath ?? string.Empty;
        _executable.SelectedItem = _executable.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string ?? string.Empty, selectedExe, StringComparison.OrdinalIgnoreCase)) ?? _executable.Items.FirstOrDefault();
        _password.Password = string.Empty;
        _hasSavedCredential = _credentialIds.Contains(profile.Id);
        _saveCredential.IsChecked = _hasSavedCredential;
        _credentialStatus.Text = _hasSavedCredential
            ? "Credential is marked as saved. RVNEX reads it only when a collection or connection test starts."
            : "No saved credential. Manual collection will request one; Get'em All will skip this profile.";
        _resultStatus.Text = profile.LastErrorSummary is null ? profile.LatestResult ?? "Connection never tested" : $"{profile.LatestResult}: {profile.LastErrorSummary}";
        _history.Items.Clear();
        foreach (ConnectionAttempt attempt in profile.Attempts.OrderByDescending(a => a.EndedUtc))
        {
            var attemptPanel = new StackPanel { Spacing = 3, Padding = new Thickness(3, 4, 3, 4) };
            attemptPanel.Children.Add(new TextBlock { Text = $"{attempt.EndedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {attempt.Status}", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
            if (!string.IsNullOrWhiteSpace(attempt.ReportPath))
            {
                string reportPath = attempt.ReportPath;
                var reportButton = new Button { Content = reportPath, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2), FontSize = 10 };
                reportButton.Click += (_, _) => _openReport(reportPath);
                attemptPanel.Children.Add(reportButton);
            }
            if (!string.IsNullOrWhiteSpace(attempt.Diagnostic)) attemptPanel.Children.Add(new TextBlock { Text = attempt.Diagnostic, TextWrapping = TextWrapping.Wrap, FontSize = 10 });
            _history.Items.Add(attemptPanel);
        }
        _loading = false;
        _ = RefreshCredentialStatusAsync(profile);
        _select(profile);
    }

    private async Task RefreshCredentialStatusAsync(ConnectionProfile profile)
    {
        try
        {
            bool has = await _hasCredential(profile);
            if (_selected?.Id != profile.Id) return;
            _hasSavedCredential = has;
            if (has) _credentialIds.Add(profile.Id); else _credentialIds.Remove(profile.Id);
            _saveCredential.IsChecked = has;
            _credentialStatus.Text = has
                ? "Credential is marked as saved. RVNEX reads it only when a collection or connection test starts."
                : "No saved credential. Manual collection will request one; Get'em All will skip this profile.";
            UpdateMetrics();
        }
        catch (Exception ex) { _credentialStatus.Text = "Windows Credential Manager is unavailable: " + ex.Message; }
    }

    private void NewProfile()
    {
        _loading = true; _selected = new ConnectionProfile { Name = "New connection", Address = string.Empty, Username = string.Empty, Port = 443 };
        _name.Text = string.Empty; _endpointType.SelectedIndex = 0; _address.Text = string.Empty; _port.Text = "443";
        _username.Text = string.Empty; _group.Text = string.Empty; _notes.Text = string.Empty; _enabled.IsChecked = true;
        _executable.SelectedIndex = 0; _saveCredential.IsChecked = false; _password.Password = string.Empty;
        _hasSavedCredential = false; _credentialStatus.Text = "Credentials are not saved unless you explicitly check the secure-save option.";
        _history.Items.Clear(); _resultStatus.Text = "New profile"; _list.SelectedItem = null; _loading = false;
        _select(null);
    }

    private async Task SaveSelectedAsync()
    {
        try
        {
            if (_selected is null) _selected = new ConnectionProfile();
            if (!int.TryParse(_port.Text, out int port)) throw new InvalidOperationException("Enter a valid port number.");
            _selected.Name = _name.Text; _selected.EndpointType = _endpointType.SelectedIndex == 1 ? EndpointType.StandaloneEsxi : EndpointType.VCenterServer;
            _selected.Address = _address.Text; _selected.Port = port; _selected.Username = _username.Text; _selected.Group = _group.Text;
            _selected.Notes = _notes.Text; _selected.Enabled = _enabled.IsChecked == true;
            _selected.UseSslCertificateOverride = _allowSslOverride.IsChecked == true;
            _selected.RvToolsExecutablePath = (_executable.SelectedItem as ComboBoxItem)?.Tag as string;
            string secret = _password.Password;
            await _save(_selected, secret, _saveCredential.IsChecked == true);
            if (!_profiles.Any(p => p.Id == _selected.Id)) _profiles.Add(_selected);
            _password.Password = string.Empty;
            _resultStatus.Text = "Profile saved. No password was written to the profile catalog.";
            _resultStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorSuccessBrush");
            RenderProfiles(); UpdateMetrics(); _select(_selected);
            _list.SelectedItem = _list.Items.OfType<ListViewItem>().FirstOrDefault(i => (i.Tag as ConnectionProfile)?.Id == _selected.Id);
            await RefreshCredentialStatusAsync(_selected);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task DuplicateSelectedAsync()
    {
        if (_selected is null || !_profiles.Any(p => p.Id == _selected.Id)) return;
        try { _selected = await _duplicate(_selected.Id); _profiles.Add(_selected); if (await _hasCredential(_selected)) _credentialIds.Add(_selected.Id); RenderProfiles(); UpdateMetrics(); _list.SelectedItem = _list.Items.OfType<ListViewItem>().FirstOrDefault(i => (i.Tag as ConnectionProfile)?.Id == _selected.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selected is null || !_profiles.Any(p => p.Id == _selected.Id)) return;
        var dialog = new ContentDialog { Title = "Delete connection profile?", Content = "This removes the profile and its associated Windows Credential Manager entry. Existing reports remain in history.", PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot, RequestedTheme = RequestedTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await _delete(_selected.Id); _credentialIds.Remove(_selected.Id); _profiles.RemoveAll(p => p.Id == _selected.Id); _selected = null; RenderProfiles(); UpdateMetrics(); NewProfile(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task MoveSelectedAsync(int delta)
    {
        if (_selected is null) return;
        try { await _move(_selected.Id, delta); RenderProfiles(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task TestSelectedAsync()
    {
        if (_selected is null) return;
        string secret = _password.Password;
        await SaveSelectedAsync();
        try { _resultStatus.Text = "Running an authenticated RVTools export check..."; await _test(_selected, secret); _password.Password = string.Empty; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task CollectSelectedAsync()
    {
        if (_selected is null) return;
        string secret = _password.Password;
        await SaveSelectedAsync();
        try { await _collect(_selected, secret); _password.Password = string.Empty; }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private string Health(ConnectionProfile profile)
    {
        if (!profile.Enabled) return "Disabled";
        if (!_credentialIds.Contains(profile.Id)) return "Missing credentials";
        if ((string.IsNullOrWhiteSpace(profile.RvToolsExecutablePath) && _executables.Count == 0) || (!string.IsNullOrWhiteSpace(profile.RvToolsExecutablePath) && !File.Exists(profile.RvToolsExecutablePath))) return "Missing or unsupported RVTools";
        if (profile.LatestResult == "MissingRvTools") return "Missing or unsupported RVTools";
        if (profile.LatestResult == "UnsupportedPort") return "Unsupported server port";
        if (profile.LatestResult == "ConnectionTestSucceeded") return "Connection test succeeded";
        if (profile.LatestResult?.StartsWith("Failed", StringComparison.Ordinal) == true || profile.LatestResult == "UnresolvedTlsFailure" || profile.LatestResult == "TimedOut" || profile.LatestResult == "FailedExecution") return "Last collection failed";
        if (profile.LatestResult == "Succeeded") return "Last collection succeeded";
        return profile.LastAttemptUtc is null || profile.LatestResult == "ConnectionTestSucceeded" ? "Connection never tested" : profile.LatestResult ?? "Connection never tested";
    }

    private void UpdateMetrics()
    {
        int enabled = _profiles.Count(p => p.Enabled);
        int distinct = _profiles.Select(p => p.Address.ToLowerInvariant()).Distinct().Count();
        int missing = _profiles.Count(p => !_credentialIds.Contains(p.Id));
        int unsuccessful = _profiles.Count(p => p.LastSuccessUtc is null);
        int recurring = _profiles.Count(p => p.Attempts.Count(a => a.Status.StartsWith("Failed", StringComparison.Ordinal) || a.Status is "UnresolvedTlsFailure" or "TimedOut") >= 2);
        int today = _profiles.Sum(p => p.Attempts.Count(a => a.Status == "Succeeded" && a.EndedUtc.ToLocalTime().Date == DateTime.Today));
        DateTime? lastSuccess = _profiles.Select(p => p.LastSuccessUtc).Where(d => d.HasValue).Max();
        DateTime? lastFailure = _profiles.SelectMany(p => p.Attempts).Where(a => a.Status.StartsWith("Failed", StringComparison.Ordinal) || a.Status is "UnresolvedTlsFailure" or "TimedOut").Select(a => (DateTime?)a.EndedUtc).Max();
        string batch = _mostRecentBatch is null ? "never" : $"{_mostRecentBatch.EndedUtc.ToLocalTime():g} ({_mostRecentBatch.Succeeded} succeeded, {_mostRecentBatch.Failed} failed, {_mostRecentBatch.Skipped} skipped, {_mostRecentBatch.Cancelled} cancelled)";
        _metrics.Text = $"Profiles {_profiles.Count}  ·  Enabled {enabled}  ·  Distinct endpoints {distinct}  ·  Reports today {today}  ·  Missing credentials {missing}  ·  No successful collection {unsuccessful}  ·  Recurring failures {recurring}  ·  Last success {lastSuccess?.ToLocalTime().ToString("g") ?? "never"}  ·  Last failure {lastFailure?.ToLocalTime().ToString("g") ?? "never"}  ·  Most recent batch {batch}";
    }

    private void ShowError(string message)
    {
        _resultStatus.Text = message;
        _resultStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorCriticalBrush");
    }
}

internal sealed record BatchProgressRow(string ProfileId, string Name, string Endpoint, string Status, string Details, DateTime? StartedUtc, DateTime? EndedUtc);

internal sealed class BatchProgressPage : UserControl
{
    private readonly TextBlock _summary = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly ListView _rows = new() { SelectionMode = ListViewSelectionMode.Single, MinHeight = 400 };
    private readonly TextBlock _elapsed = new() { FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") };
    private readonly Action _retryFailed;
    private readonly Func<string, Task> _retrySelected;
    private readonly Button _retryFailedButton = new() { Content = "Retry failed targets" };
    private readonly Button _retrySelectedButton = new() { Content = "Retry selected target" };

    public BatchProgressPage(Action retryFailed, Func<string, Task> retrySelected)
    {
        _retryFailed = retryFailed; _retrySelected = retrySelected;
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(OverviewPage.PageHeader("Get'em All", "Sequential collection run. Per-target progress is based on process state; RVTools does not expose a reliable export percentage."));
        root.Children.Add(_summary); root.Children.Add(_progress); root.Children.Add(_elapsed); root.Children.Add(_rows);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _retryFailedButton.Click += (_, _) => _retryFailed();
        _retrySelectedButton.Click += async (_, _) =>
        {
            if ((_rows.SelectedItem as ListViewItem)?.Tag is BatchProgressRow row && row.Status is "Failed" or "Timed out") await _retrySelected(row.ProfileId);
        };
        actions.Children.Add(_retryFailedButton); actions.Children.Add(_retrySelectedButton); root.Children.Add(actions);
        Content = root;
    }

    public void Refresh(IReadOnlyList<BatchProgressRow> items, int configured, int enabled, int eligible, bool running, TimeSpan elapsed)
    {
        string? selectedId = (_rows.SelectedItem as ListViewItem)?.Tag is BatchProgressRow selected ? selected.ProfileId : null;
        int completed = items.Count(i => i.Status is "Succeeded" or "Failed" or "Timed out" or "Skipped" or "Cancelled");
        int succeeded = items.Count(i => i.Status == "Succeeded");
        int failed = items.Count(i => i.Status == "Failed" || i.Status == "Timed out");
        int skipped = items.Count(i => i.Status == "Skipped");
        int cancelled = items.Count(i => i.Status == "Cancelled");
        _summary.Text = $"Configured {configured}  ·  Enabled {enabled}  ·  Eligible {eligible}  ·  Completed {completed}/{items.Count}  ·  Succeeded {succeeded}  ·  Failed {failed}  ·  Skipped {skipped}  ·  Cancelled {cancelled}";
        _elapsed.Text = $"Overall elapsed {elapsed:hh\\:mm\\:ss}";
        _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _retryFailedButton.IsEnabled = !running && failed > 0;
        _retrySelectedButton.IsEnabled = !running && selectedId is not null;
        _rows.Items.Clear();
        foreach (BatchProgressRow item in items)
        {
            string perTarget = item.StartedUtc is null ? "Not started" : item.EndedUtc is null ? $"Running for {DateTime.UtcNow - item.StartedUtc.Value:hh\\:mm\\:ss}" : $"Elapsed {item.EndedUtc.Value - item.StartedUtc.Value:hh\\:mm\\:ss}";
            var panel = new StackPanel { Spacing = 3, Padding = new Thickness(6, 7, 6, 7) };
            panel.Children.Add(new TextBlock { Text = $"{item.Name}  ·  {item.Status}  ·  {perTarget}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(new TextBlock { Text = item.Endpoint, FontSize = 11, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") });
            if (!string.IsNullOrWhiteSpace(item.Details)) panel.Children.Add(new TextBlock { Text = item.Details, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var listItem = new ListViewItem { Content = panel, Tag = item };
            _rows.Items.Add(listItem);
            if (item.ProfileId == selectedId) _rows.SelectedItem = listItem;
        }
    }
}
