using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RVNEX.Models;
using RVNEX.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RVNEX;

internal sealed class OverviewPage : UserControl
{
    public OverviewPage(SnapshotRecord snapshot, WorkbookData workbook, InventoryAnalytics analytics)
    {
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(PageHeader("Infrastructure overview", snapshot.Name,
            $"{snapshot.SourceAddress ?? "Source endpoint unavailable"}  ·  {SnapshotDate(snapshot)}"));

        IReadOnlyDictionary<string, string> metrics = analytics.GetMetrics(workbook);
        var cards = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (int column = 0; column < 3; column++)
        {
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        int metricIndex = 0;
        foreach ((string label, string value) in metrics.Where(pair => pair.Key != "Capacity coverage"))
        {
            var card = new Border
            {
                Padding = new Thickness(16, 13, 16, 13),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                Background = ThemeBrush("CardBackgroundFillColorDefaultBrush")
            };
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = value, FontSize = 25, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            card.Child = stack;
            Grid.SetColumn(card, metricIndex % 3);
            Grid.SetRow(card, metricIndex / 3);
            cards.Children.Add(card);
            metricIndex++;
        }
        int rowCount = Math.Max(1, (metricIndex + 2) / 3);
        for (int row = 0; row < rowCount; row++) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(cards);

        var distributions = new Grid { ColumnSpacing = 12 };
        distributions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        distributions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var storageSurface = Surface();
        var storageStack = new StackPanel { Spacing = 8 };
        storageStack.Children.Add(new TextBlock { Text = "Datastore space", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        storageStack.Children.Add(new TextBlock { Text = $"{metrics["Datastore used"]} used · {metrics["Datastore free"]} free", FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        if (decimal.TryParse(metrics["Datastore utilization"].TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out decimal utilization))
        {
            storageStack.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = (double)utilization, Height = 6 });
            storageStack.Children.Add(new TextBlock { Text = $"{metrics["Datastore utilization"]} of reported capacity used", FontSize = 11, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        }
        else
        {
            storageStack.Children.Add(new TextBlock { Text = "Capacity coverage is unavailable in this workbook.", FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        }
        storageSurface.Child = storageStack;
        Grid.SetColumn(storageSurface, 0);
        distributions.Children.Add(storageSurface);

        var powerSurface = Surface();
        var powerStack = new StackPanel { Spacing = 8 };
        powerStack.Children.Add(new TextBlock { Text = "VM power states", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        powerStack.Children.Add(new TextBlock { Text = $"{metrics["Powered on"]} powered on · {metrics["Powered off"]} powered off", FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        int poweredOn = 0;
        int poweredOff = 0;
        bool powerAvailable = int.TryParse(metrics["Powered on"], NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out poweredOn)
            && int.TryParse(metrics["Powered off"], NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out poweredOff)
            && poweredOn + poweredOff > 0;
        if (powerAvailable)
        {
            powerStack.Children.Add(new ProgressBar { Minimum = 0, Maximum = poweredOn + poweredOff, Value = poweredOn, Height = 6 });
            powerStack.Children.Add(new TextBlock { Text = $"{poweredOn:N0} of {poweredOn + poweredOff:N0} VMs were powered on in this snapshot.", FontSize = 11, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        }
        else
        {
            powerStack.Children.Add(new TextBlock { Text = "Power state coverage is unavailable or empty.", FontSize = 12, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        }
        powerSurface.Child = powerStack;
        Grid.SetColumn(powerSurface, 1);
        distributions.Children.Add(powerSurface);
        content.Children.Add(distributions);

        var findingsSurface = Surface();
        var findingsStack = new StackPanel { Spacing = 8 };
        findingsStack.Children.Add(new TextBlock { Text = "Inventory findings", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (string finding in analytics.GetFindings(workbook))
        {
            findingsStack.Children.Add(new TextBlock
            {
                Text = $"• {finding}",
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        findingsSurface.Child = findingsStack;
        content.Children.Add(findingsSurface);

        var coverage = Surface();
        var coverageStack = new StackPanel { Spacing = 10 };
        coverageStack.Children.Add(new TextBlock { Text = "Workbook coverage", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        coverageStack.Children.Add(new TextBlock
        {
            Text = $"{workbook.Worksheets.Count} readable worksheets · {workbook.Worksheets.Sum(sheet => sheet.Rows.Count):N0} inventory rows",
            FontSize = 13,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
        var sheetGrid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
        sheetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sheetGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int sheetIndex = 0;

        foreach (WorksheetData sheet in workbook.Worksheets)
        {
            int row = sheetIndex / 2;
            if (row >= sheetGrid.RowDefinitions.Count)
            {
                sheetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var sheetTag = new Border
            {
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(9, 5, 9, 5),
                CornerRadius = new CornerRadius(6),
                Background = ThemeBrush("LayerFillColorAltBrush"),
                Child = new TextBlock { Text = $"{sheet.Name} · {sheet.Rows.Count:N0}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
            };
            Grid.SetRow(sheetTag, row);
            Grid.SetColumn(sheetTag, sheetIndex % 2);
            sheetGrid.Children.Add(sheetTag);
            sheetIndex++;
        }
        coverageStack.Children.Add(sheetGrid);
        if (!string.IsNullOrWhiteSpace(metrics["Capacity coverage"]))
        {
            coverageStack.Children.Add(new TextBlock
            {
                Text = metrics["Capacity coverage"],
                FontSize = 12,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (string warning in workbook.Warnings)
        {
            coverageStack.Children.Add(new TextBlock { Text = warning, FontSize = 12, Foreground = ThemeBrush("SystemFillColorCautionBrush"), TextWrapping = TextWrapping.Wrap });
        }
        coverage.Child = coverageStack;
        content.Children.Add(coverage);

        var note = Surface();
        note.Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Inventory snapshot, not live monitoring", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = "Dashboard values come from the selected workbook. RVTools inventory does not provide a continuous performance history, so RVNEX does not infer utilization trends, latency, or IOPS.", FontSize = 13, Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap }
            }
        };
        content.Children.Add(note);
        Content = content;
    }

    private static string SnapshotDate(SnapshotRecord record) =>
        (record.CollectedUtc ?? record.ImportedUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    internal static StackPanel PageHeader(string title, string subtitle, string? detail = null)
    {
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = subtitle, FontSize = 14, Foreground = ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            header.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = ThemeBrush("TextFillColorTertiaryBrush"), TextWrapping = TextWrapping.Wrap });
        }
        return header;
    }

    internal static Border Surface() => new()
    {
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
        Background = ThemeBrush("CardBackgroundFillColorDefaultBrush")
    };

    private static readonly IReadOnlyDictionary<string, string> ThemeBrushAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CardBackgroundFillColorDefaultBrush"] = "RvnexSurfaceBrush",
        ["CardStrokeColorDefaultBrush"] = "RvnexBorderBrush",
        ["LayerFillColorAltBrush"] = "RvnexSurfaceAltBrush",
        ["TextFillColorSecondaryBrush"] = "RvnexTextSecondaryBrush",
        ["TextFillColorTertiaryBrush"] = "RvnexTextTertiaryBrush",
        ["SystemFillColorSuccessBrush"] = "RvnexSuccessBrush",
        ["SystemFillColorCautionBrush"] = "RvnexWarningBrush",
        ["SystemFillColorCriticalBrush"] = "RvnexErrorBrush"
    };

    internal static Brush ThemeBrush(string key)
    {
        string resourceKey = ThemeBrushAliases.TryGetValue(key, out string? alias) ? alias : key;
        return Application.Current.Resources.TryGetValue(resourceKey, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}

internal sealed class InventoryPage : UserControl
{
    private readonly WorkbookData _workbook;
    private readonly InventoryAnalytics _analytics;
    private readonly ComboBox _sheetSelector = new();
    private readonly ComboBox _fieldSelector = new();
    private readonly ComboBox _operatorSelector = new();
    private readonly ComboBox _sortSelector = new();
    private readonly CheckBox _descendingCheck = new() { Content = "Descending", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _filterValue = new();
    private readonly TextBox _searchBox = new();
    private readonly ListView _filterList = new();
    private readonly ListView _rowsList = new();
    private readonly TextBlock _resultLabel = new();
    private readonly TextBlock _coverageLabel = new();
    private readonly TextBlock _detailLabel = new();
    private readonly List<InventoryFilter> _filters = [];
    private readonly List<WorksheetData> _sheets;
    private readonly IReadOnlyList<string> _joinWarnings;
    private WorksheetData? _sheet;
    private IReadOnlyList<WorksheetRow> _visibleRows = [];

    public InventoryPage(WorkbookData workbook, InventoryAnalytics analytics)
    {
        _workbook = workbook;
        _analytics = analytics;
        _sheets = workbook.Worksheets.ToList();
        WorksheetData? joinedVmSheet = analytics.CreateJoinedVmWorksheet(workbook, out _joinWarnings);
        if (joinedVmSheet is not null) _sheets.Insert(0, joinedVmSheet);
        var root = new StackPanel { Spacing = 14 };
        root.Children.Add(OverviewPage.PageHeader("Inventory explorer", "Search every readable worksheet and combine filter clauses with AND. VM inventory joins related disk, NIC, snapshot, and guest worksheets where VM identity is unique.", Path.GetFileName(workbook.SourcePath)));

        var explorer = OverviewPage.Surface();
        var panel = new StackPanel { Spacing = 10 };
        var sheetRow = new Grid { ColumnSpacing = 10 };
        sheetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sheetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        sheetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        sheetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sheetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _sheetSelector.ItemsSource = _sheets.Select(sheet => sheet.Name).ToArray();
        _sheetSelector.SelectedIndex = _sheets.Count > 0 ? 0 : -1;
        _sheetSelector.SelectionChanged += (_, _) => SelectSheet();
        Grid.SetColumn(_sheetSelector, 0);
        sheetRow.Children.Add(_sheetSelector);
        _searchBox.PlaceholderText = "Search all columns in this worksheet";
        _searchBox.TextChanged += (_, _) => RefreshRows();
        Grid.SetColumn(_searchBox, 1);
        sheetRow.Children.Add(_searchBox);
        _sortSelector.PlaceholderText = "Sort by";
        _sortSelector.SelectionChanged += (_, _) => RefreshRows();
        Grid.SetColumn(_sortSelector, 2);
        sheetRow.Children.Add(_sortSelector);
        _descendingCheck.Checked += (_, _) => RefreshRows();
        _descendingCheck.Unchecked += (_, _) => RefreshRows();
        Grid.SetColumn(_descendingCheck, 3);
        sheetRow.Children.Add(_descendingCheck);
        var exportButton = new Button { Content = "Export filtered CSV" };
        exportButton.Click += (_, _) => ExportCsv();
        Grid.SetColumn(exportButton, 4);
        sheetRow.Children.Add(exportButton);
        panel.Children.Add(sheetRow);

        var filterRow = new Grid { ColumnSpacing = 8 };
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        filterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _fieldSelector.PlaceholderText = "Field";
        _operatorSelector.ItemsSource = new[] { "Contains", "Equals", "Starts with", "Not contains", "Greater than", "Less than", "Is empty", "Is not empty" };
        _operatorSelector.SelectedIndex = 0;
        _filterValue.PlaceholderText = "Value";
        var addFilterButton = new Button { Content = "Add filter" };
        addFilterButton.Click += (_, _) => AddFilter();
        Grid.SetColumn(_fieldSelector, 0); filterRow.Children.Add(_fieldSelector);
        Grid.SetColumn(_operatorSelector, 1); filterRow.Children.Add(_operatorSelector);
        Grid.SetColumn(_filterValue, 2); filterRow.Children.Add(_filterValue);
        Grid.SetColumn(addFilterButton, 3); filterRow.Children.Add(addFilterButton);
        panel.Children.Add(filterRow);

        var filterHeader = new Grid();
        filterHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filterHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filterHeader.Children.Add(new TextBlock { Text = "Active filters (AND)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var resetButton = new Button { Content = "Reset filters", Padding = new Thickness(8, 3, 8, 3) };
        resetButton.Click += (_, _) => { _filters.Clear(); UpdateFilterList(); RefreshRows(); };
        Grid.SetColumn(resetButton, 1);
        filterHeader.Children.Add(resetButton);
        panel.Children.Add(filterHeader);
        _filterList.MaxHeight = 74;
        _filterList.SelectionMode = ListViewSelectionMode.Single;
        _filterList.ItemsSource = Array.Empty<string>();
        var removeFilter = new Button { Content = "Remove selected" };
        removeFilter.Click += (_, _) =>
        {
            if (_filterList.SelectedIndex >= 0 && _filterList.SelectedIndex < _filters.Count)
            {
                _filters.RemoveAt(_filterList.SelectedIndex);
                UpdateFilterList();
                RefreshRows();
            }
        };
        var filterActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        filterActions.Children.Add(_filterList);
        filterActions.Children.Add(removeFilter);
        panel.Children.Add(filterActions);

        _resultLabel.FontSize = 12;
        _resultLabel.Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush");
        panel.Children.Add(_resultLabel);
        _coverageLabel.FontSize = 11;
        _coverageLabel.Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush");
        _coverageLabel.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_coverageLabel);
        _rowsList.SelectionMode = ListViewSelectionMode.Single;
        _rowsList.FontFamily = new FontFamily("Consolas");
        _rowsList.FontSize = 11;
        _rowsList.MaxHeight = 570;
        _rowsList.SelectionChanged += (_, _) => ShowSelectedRow();
        panel.Children.Add(_rowsList);
        _detailLabel.FontSize = 12;
        _detailLabel.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_detailLabel);
        explorer.Child = panel;
        root.Children.Add(explorer);
        Content = root;
        SelectSheet();
    }

    private void SelectSheet()
    {
        string? name = _sheetSelector.SelectedItem as string;
        _sheet = _sheets.FirstOrDefault(sheet => sheet.Name == name);
        _filters.Clear();
        _fieldSelector.ItemsSource = _sheet?.Headers ?? [];
        _fieldSelector.SelectedIndex = _sheet?.Headers.Count > 0 ? 0 : -1;
        _sortSelector.ItemsSource = _sheet?.Headers ?? [];
        _sortSelector.SelectedIndex = -1;
        _coverageLabel.Text = _sheet?.Name == "VM inventory (joined)"
            ? (_joinWarnings.Count == 0 ? "VM-related fields were joined by a stable identifier or an exact VM name unique in the VM worksheet. Review raw worksheets for source details." : string.Join(" ", _joinWarnings))
            : string.Empty;
        UpdateFilterList();
        RefreshRows();
    }

    private void AddFilter()
    {
        if (_sheet is null || _fieldSelector.SelectedItem is not string field || _operatorSelector.SelectedItem is not string operation)
        {
            return;
        }
        if (operation is not ("Is empty" or "Is not empty") && string.IsNullOrWhiteSpace(_filterValue.Text))
        {
            _resultLabel.Text = "Enter a value for this filter.";
            return;
        }
        _filters.Add(new InventoryFilter(field, operation, _filterValue.Text ?? string.Empty));
        _filterValue.Text = string.Empty;
        UpdateFilterList();
        RefreshRows();
    }

    private void UpdateFilterList() =>
        _filterList.ItemsSource = _filters.Select(filter => $"{filter.Field} {filter.Operator} {filter.Value}".Trim()).ToArray();

    private void RefreshRows()
    {
        if (_sheet is null)
        {
            _rowsList.ItemsSource = Array.Empty<string>();
            _resultLabel.Text = "This workbook has no readable worksheets.";
            return;
        }
        _visibleRows = _analytics.ApplyFilters(_sheet, _sheet.Rows, _filters, _searchBox.Text);
        if (_sortSelector.SelectedItem is string sortField)
        {
            int sortColumn = _sheet.Headers.ToList().FindIndex(header => header.Equals(sortField, StringComparison.OrdinalIgnoreCase));
            if (sortColumn >= 0)
            {
                var comparer = Comparer<WorksheetRow>.Create((left, right) =>
                {
                    string leftValue = sortColumn < left.Values.Count ? left.Values[sortColumn] : string.Empty;
                    string rightValue = sortColumn < right.Values.Count ? right.Values[sortColumn] : string.Empty;
                    bool leftNumeric = decimal.TryParse(leftValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal leftNumber);
                    bool rightNumeric = decimal.TryParse(rightValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal rightNumber);
                    if (leftNumeric && rightNumeric) return leftNumber.CompareTo(rightNumber);
                    return StringComparer.OrdinalIgnoreCase.Compare(leftValue, rightValue);
                });
                _visibleRows = (_descendingCheck.IsChecked == true
                    ? _visibleRows.OrderByDescending(row => row, comparer)
                    : _visibleRows.OrderBy(row => row, comparer)).ToArray();
            }
        }
        _resultLabel.Text = $"{_visibleRows.Count:N0} of {_sheet.Rows.Count:N0} rows · { _sheet.Headers.Count:N0} source columns";
        _rowsList.ItemsSource = _visibleRows.Select(row => string.Join("    ", _sheet.Headers.Select((header, index) =>
            $"{header}: {(index < row.DisplayValues.Count ? row.DisplayValues[index] : index < row.Values.Count ? row.Values[index] : string.Empty)}"))).ToArray();
        ShowSelectedRow();
    }

    private void ShowSelectedRow()
    {
        if (_sheet is null || _rowsList.SelectedIndex < 0 || _rowsList.SelectedIndex >= _visibleRows.Count)
        {
            _detailLabel.Text = string.Empty;
            return;
        }
        WorksheetRow row = _visibleRows[_rowsList.SelectedIndex];
        _detailLabel.Text = $"Source row {row.SourceRowNumber} · " + string.Join("  ·  ",
            _sheet.Headers.Select((header, index) => $"{header}={(index < row.Values.Count ? row.Values[index] : string.Empty)}"));
    }

    private void ExportCsv()
    {
        if (_sheet is null) return;
        try
        {
            string outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RVNEX Reports");
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, $"RVNEX_Filtered_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.WriteLine(string.Join(",", _sheet.Headers.Select(EscapeCsv)));
            foreach (WorksheetRow row in _visibleRows)
            {
                writer.WriteLine(string.Join(",", _sheet.Headers.Select((_, index) => EscapeCsv(index < row.Values.Count ? row.Values[index] : string.Empty))));
            }
            _resultLabel.Text = $"{_visibleRows.Count:N0} filtered rows exported to {outputPath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _resultLabel.Text = $"CSV export failed: {exception.Message}";
        }
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

internal sealed class ComparisonPage : UserControl
{
    public ComparisonPage(
        IReadOnlyList<(SnapshotRecord Record, WorkbookData Workbook)> snapshots,
        InventoryAnalytics analytics,
        string? baselineId,
        Action<string> baselineChanged)
    {
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(OverviewPage.PageHeader("Snapshot comparison", $"{snapshots.Count} reports selected · changes are computed from saved workbook data."));
        if (snapshots.Count < 2)
        {
            root.Children.Add(new TextBlock { Text = "Select at least two reports from history to compare snapshots.", FontSize = 14 });
            Content = root;
            return;
        }

        var baselineSelector = new ComboBox { Header = "Baseline snapshot", ItemsSource = snapshots.Select(item => $"{item.Record.Id}|{item.Record.Name}").ToArray() };
        int baselineIndex = snapshots.ToList().FindIndex(item => item.Record.Id == baselineId);
        baselineSelector.SelectedIndex = baselineIndex >= 0 ? baselineIndex : 0;
        baselineSelector.SelectionChanged += (_, _) =>
        {
            if (baselineSelector.SelectedItem is string selected) baselineChanged(selected.Split('|', 2)[0]);
        };
        root.Children.Add(baselineSelector);

        int selectedBaselineIndex = baselineSelector.SelectedIndex < 0 ? 0 : baselineSelector.SelectedIndex;
        (SnapshotRecord baselineRecord, WorkbookData baselineWorkbook) = snapshots[selectedBaselineIndex];
        var targets = snapshots.Where((_, index) => index != selectedBaselineIndex)
            .OrderBy(item => item.Record.CollectedUtc ?? item.Record.ImportedUtc).ToArray();
        bool sameEnvironment = !string.IsNullOrWhiteSpace(baselineRecord.SourceAddress)
            && targets.All(item => string.Equals(item.Record.SourceAddress, baselineRecord.SourceAddress, StringComparison.OrdinalIgnoreCase));

        var allChanges = new List<(string Target, EntityChange Change)>();
        foreach ((SnapshotRecord record, WorkbookData workbook) in targets)
        {
            allChanges.AddRange(analytics.Compare(baselineWorkbook, workbook, sameEnvironment).Select(change => (record.Name, change)));
        }

        var info = OverviewPage.Surface();
        info.Child = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = $"Baseline: {baselineRecord.Name}", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = sameEnvironment ? $"Environment: {baselineRecord.SourceAddress} · additions and removals can be classified against this source." : "Environment identity is unavailable or differs. Changes are shown as observed configuration differences; absence is not asserted as deletion.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap }
            }
        };
        root.Children.Add(info);

        string[] categories = ["Added", "Removed", "Modified", "Observed in target", "Not observed in target", "Ambiguous identity", "Coverage unavailable"];
        var cards = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 4; i++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (categories.Length + 3) / 4; i++) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < categories.Length; i++)
        {
            int count = allChanges.Count(item => item.Change.ChangeType == categories[i]);
            var card = OverviewPage.Surface();
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(new TextBlock { Text = categories[i], FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") });
            stack.Children.Add(new TextBlock { Text = count.ToString("N0", CultureInfo.CurrentCulture), FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            card.Child = stack;
            Grid.SetColumn(card, i % 4);
            Grid.SetRow(card, i / 4);
            cards.Children.Add(card);
        }
        root.Children.Add(cards);

        var changesSurface = OverviewPage.Surface();
        var changesPanel = new StackPanel { Spacing = 8 };
        changesPanel.Children.Add(new TextBlock { Text = "Entity and field changes", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var exportPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var exportChangesButton = new Button { Content = "Export comparison CSV" };
        var exportStatus = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        exportChangesButton.Click += (_, _) => ExportChanges(allChanges, exportStatus);
        exportPanel.Children.Add(exportChangesButton);
        exportPanel.Children.Add(exportStatus);
        changesPanel.Children.Add(exportPanel);
        var changeList = new ListView { MaxHeight = 590 };
        changeList.ItemsSource = allChanges.Select(item =>
        {
            string fields = item.Change.Fields.Count == 0
                ? string.Empty
                : " · " + string.Join("; ", item.Change.Fields.Take(5).Select(FormatFieldChange));
            return $"{item.Target} · {item.Change.Kind} · {item.Change.ChangeType} · {item.Change.Name} · confidence {item.Change.Confidence}{fields}";
        }).ToArray();
        changesPanel.Children.Add(changeList);
        if (allChanges.Count == 0)
        {
            changesPanel.Children.Add(new TextBlock { Text = "No comparable core-entity changes were found in the available worksheets.", Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") });
        }
        changesSurface.Child = changesPanel;
        root.Children.Add(changesSurface);
        Content = root;
    }

    private static string Short(string value) => value.Length > 70 ? value[..67] + "..." : value;

    private static string FormatFieldChange(FieldChange field)
    {
        string delta = string.Empty;
        if (decimal.TryParse(field.BaselineValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal baseline)
            && decimal.TryParse(field.TargetValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal target))
        {
            decimal difference = target - baseline;
            string percent = baseline == 0 ? string.Empty : $"; {difference / Math.Abs(baseline) * 100m:+0.##;-0.##;0}%";
            delta = $" (Δ {difference:+0.##;-0.##;0}{percent})";
        }
        return $"{field.Field}: {Short(field.BaselineValue)} → {Short(field.TargetValue)}{delta}";
    }

    private static void ExportChanges(IReadOnlyList<(string Target, EntityChange Change)> changes, TextBlock status)
    {
        try
        {
            string outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RVNEX Reports");
            Directory.CreateDirectory(outputDirectory);
            string path = Path.Combine(outputDirectory, $"RVNEX_Comparison_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.WriteLine(string.Join(",", new[] { "Target snapshot", "Entity type", "Change", "Name", "Confidence", "Field", "Baseline value", "Target value" }.Select(EscapeCsv)));
            foreach ((string targetName, EntityChange change) in changes)
            {
                if (change.Fields.Count == 0)
                {
                    writer.WriteLine(string.Join(",", new[] { targetName, change.Kind, change.ChangeType, change.Name, change.Confidence, string.Empty, string.Empty, string.Empty }.Select(EscapeCsv)));
                    continue;
                }
                foreach (FieldChange field in change.Fields)
                {
                    writer.WriteLine(string.Join(",", new[] { targetName, change.Kind, change.ChangeType, change.Name, change.Confidence, field.Field, field.BaselineValue, field.TargetValue }.Select(EscapeCsv)));
                }
            }
            status.Text = $"Exported {changes.Count:N0} entity changes to {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            status.Text = $"Comparison export failed: {exception.Message}";
        }
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

internal sealed class ReportsPage : UserControl
{
    public ReportsPage(SnapshotRecord? record, Action refresh, Action open, Action rename, Action remove)
    {
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(OverviewPage.PageHeader("Reports & history", "Manage the local snapshot catalog. Removing a record never deletes its workbook."));
        var refreshButton = new Button { Content = "Refresh catalog" };
        refreshButton.Click += (_, _) => refresh();
        root.Children.Add(refreshButton);
        if (record is null)
        {
            root.Children.Add(new TextBlock { Text = "No report is selected. Import an RVTools .xlsx workbook to begin working offline.", FontSize = 14, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var card = OverviewPage.Surface();
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock { Text = record.Name, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"Workbook: {record.WorkbookPath}", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = $"Source: {record.SourceAddress ?? "Not recorded in imported workbook"}" });
            string imported = record.ImportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            string collected = record.CollectedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "Unknown";
            content.Children.Add(new TextBlock { Text = $"Imported: {imported} · Collected: {collected}" });
            content.Children.Add(new TextBlock { Text = $"RVTools: {record.RvToolsVersion ?? "Unknown"} · SHA-256: {record.Sha256}", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            content.Children.Add(new TextBlock { Text = $"VMs {record.VmCount:N0} · Hosts {record.HostCount:N0} · Clusters {record.ClusterCount:N0} · Datastores {record.DatastoreCount:N0} · Networks {record.NetworkCount:N0}" });
            content.Children.Add(new TextBlock { Text = record.FileAvailable ? "Workbook available" : "Workbook file is missing", Foreground = record.FileAvailable ? OverviewPage.ThemeBrush("SystemFillColorSuccessBrush") : OverviewPage.ThemeBrush("SystemFillColorCautionBrush") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var openButton = new Button { Content = "Open workbook" }; openButton.Click += (_, _) => open();
            var renameButton = new Button { Content = "Rename record" }; renameButton.Click += (_, _) => rename();
            var removeButton = new Button { Content = "Remove from catalog", Style = (Style)Application.Current.Resources["RvnexDestructiveButtonStyle"] }; removeButton.Click += (_, _) => remove();
            buttons.Children.Add(openButton); buttons.Children.Add(renameButton); buttons.Children.Add(removeButton);
            content.Children.Add(buttons);
            card.Child = content;
            root.Children.Add(card);
        }
        Content = root;
    }
}

internal sealed class SettingsPage : UserControl
{
    public SettingsPage(int timeoutMinutes, string theme, Action<int> saveTimeout, Action<string> saveTheme, bool confirmBulkCollection = false, Action<bool>? saveBulkConfirmation = null, int maximumRetries = 0, Action<int>? saveMaximumRetries = null)
    {
        var root = new StackPanel { Spacing = 16 };
        root.Children.Add(OverviewPage.PageHeader("Settings & diagnostics", "Local storage, collector discovery, and security information."));
        string? executable = RvToolsInstallationLocator.FindExecutable();
        string version = executable is null ? "Not installed" : FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "Unknown";
        var collector = OverviewPage.Surface();
        collector.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "RVTools", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = $"Executable: {executable ?? "Not found"}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Detected version: {version}" },
                new TextBlock { Text = "RVTools 4.5+ uses ExportAll2xlsx and a DPAPI-protected -p value. RVNEX accepts a collection only after RVTools exits successfully and the generated workbook is read and registered.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "RVTools 4.8.1+ validates server certificates by default. The log4net configuration warning alone does not establish why a connection failed. Resolve the server certificate trust through your normal security process, or explicitly allow the per-profile SSL override only after verifying the server identity. RVNEX never enables the override automatically.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap }
            }
        };
        root.Children.Add(collector);

        var operation = OverviewPage.Surface();
        var operationPanel = new StackPanel { Spacing = 8 };
        operationPanel.Children.Add(new TextBlock { Text = "Collection timeout", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        operationPanel.Children.Add(new TextBlock { Text = "RVTools does not report a reliable percentage. RVNEX uses indeterminate progress and stops the owned process tree at the configured timeout.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        var timeoutRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var timeoutInput = new TextBox { Text = timeoutMinutes.ToString(CultureInfo.InvariantCulture), Width = 90, Header = "Minutes" };
        var saveTimeoutButton = new Button { Content = "Save timeout" };
        var timeoutStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        saveTimeoutButton.Click += (_, _) =>
        {
            if (!int.TryParse(timeoutInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) || minutes is < 5 or > 240)
            {
                timeoutStatus.Text = "Enter a whole number from 5 to 240.";
                timeoutStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorCautionBrush");
                return;
            }
            try
            {
                saveTimeout(minutes);
                timeoutStatus.Text = $"Saved · {minutes} minutes";
                timeoutStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorSuccessBrush");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                timeoutStatus.Text = $"Could not save: {exception.Message}";
                timeoutStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorCriticalBrush");
            }
        };
        timeoutRow.Children.Add(timeoutInput);
        timeoutRow.Children.Add(saveTimeoutButton);
        timeoutRow.Children.Add(timeoutStatus);
        operationPanel.Children.Add(timeoutRow);
        var bulkConfirmation = new CheckBox { Content = "Ask for a separate confirmation before Get'em All", IsChecked = confirmBulkCollection };
        var bulkSave = new Button { Content = "Save batch confirmation setting", HorizontalAlignment = HorizontalAlignment.Left };
        var bulkStatus = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        bulkSave.Click += (_, _) =>
        {
            try { saveBulkConfirmation?.Invoke(bulkConfirmation.IsChecked == true); bulkStatus.Text = "Saved"; bulkStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorSuccessBrush"); }
            catch (Exception ex) { bulkStatus.Text = "Could not save: " + ex.Message; bulkStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorCriticalBrush"); }
        };
        operationPanel.Children.Add(bulkConfirmation);
        operationPanel.Children.Add(bulkSave);
        operationPanel.Children.Add(bulkStatus);
        var retryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var retryInput = new TextBox { Text = maximumRetries.ToString(CultureInfo.InvariantCulture), Width = 90, Header = "Transient retries" };
        var retrySave = new Button { Content = "Save retry limit" };
        var retryStatus = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        retrySave.Click += (_, _) =>
        {
            if (!int.TryParse(retryInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) || retries is < 0 or > 3)
            { retryStatus.Text = "Enter a whole number from zero to three."; return; }
            try { saveMaximumRetries?.Invoke(retries); retryStatus.Text = $"Saved · {retries} transient retry(s)"; }
            catch (Exception ex) { retryStatus.Text = "Could not save: " + ex.Message; }
        };
        retryRow.Children.Add(retryInput); retryRow.Children.Add(retrySave); retryRow.Children.Add(retryStatus);
        operationPanel.Children.Add(new TextBlock { Text = "Automatic retries apply only to connection failures and timeouts; authentication, TLS, and workbook errors are never retried. Default is zero.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        operationPanel.Children.Add(retryRow);
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var themeSelector = new ComboBox { Header = "Application theme", ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = theme, Width = 180 };
        var saveThemeButton = new Button { Content = "Save theme" };
        var themeStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        void SaveThemePreference()
        {
            if (themeSelector.SelectedItem is not string selectedTheme) return;
            try
            {
                saveTheme(selectedTheme);
                themeStatus.Text = $"Saved · {selectedTheme}";
                themeStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorSuccessBrush");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                themeStatus.Text = $"Could not save: {exception.Message}";
                themeStatus.Foreground = OverviewPage.ThemeBrush("SystemFillColorCriticalBrush");
            }
        }
        themeSelector.SelectionChanged += (_, _) => SaveThemePreference();
        saveThemeButton.Click += (_, _) => SaveThemePreference();
        themeRow.Children.Add(themeSelector);
        themeRow.Children.Add(saveThemeButton);
        themeRow.Children.Add(themeStatus);
        operationPanel.Children.Add(themeRow);
        operation.Child = operationPanel;
        root.Children.Add(operation);

        var security = OverviewPage.Surface();
        security.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Security and local data", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = $"Catalog: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RVNEX", "catalog.json")}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Reports: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RVNEX Reports")}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Profile metadata is stored locally without passwords. If explicitly requested, credentials are stored through Windows Credential Manager under the current Windows user. RVNEX converts passwords to RVTools' DPAPI-protected -p format before launch; plaintext is not included in RVTools arguments or saved in history, diagnostics, or reports.", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap }
            }
        };
        root.Children.Add(security);

        var about = OverviewPage.Surface();
        about.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "About RVNEX", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = AppProductInfo.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = $"Version {AppProductInfo.Version}", FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") },
                new TextBlock { Text = AppProductInfo.Author, FontSize = 12 },
                new TextBlock { Text = AppProductInfo.Website, FontSize = 12, Foreground = OverviewPage.ThemeBrush("RvnexAccentBrush") },
                new TextBlock { Text = AppProductInfo.Credits, FontSize = 12, Foreground = OverviewPage.ThemeBrush("TextFillColorSecondaryBrush") }
            }
        };
        root.Children.Add(about);
        Content = root;
    }
}
