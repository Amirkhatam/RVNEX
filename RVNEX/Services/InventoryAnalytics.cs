using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RVNEX.Services;

internal sealed class InventoryAnalytics
{
    public static readonly string[] CoreKinds = ["VM", "Host", "Cluster", "Datastore", "Network"];

    public IReadOnlyList<InventoryEntity> GetEntities(WorkbookData workbook, string? kind = null)
    {
        var entities = new List<InventoryEntity>();
        foreach (WorksheetData sheet in workbook.Worksheets)
        {
            string? sheetKind = GetKind(sheet.Name);
            if (sheetKind is null || (kind is not null && !sheetKind.Equals(kind, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (WorksheetRow row in sheet.Rows)
            {
                IReadOnlyDictionary<string, string> fields = ToFields(sheet, row);
                string name = GetName(sheetKind, fields);
                if (string.IsNullOrWhiteSpace(name)) continue;
                (string identity, string confidence) = GetIdentity(sheetKind, fields, name);
                entities.Add(new InventoryEntity(sheetKind, identity, confidence, name, sheet.Name, row.SourceRowNumber, fields));
            }
        }
        return entities;
    }

    public WorksheetData? CreateJoinedVmWorksheet(WorkbookData workbook, out IReadOnlyList<string> warnings)
    {
        var relationWarnings = new List<string>();
        WorksheetData? vmSheet = FindSheet(workbook, "VM");
        if (vmSheet is null)
        {
            warnings = relationWarnings;
            return null;
        }

        int nameIndex = FindColumnIndex(vmSheet.Headers, "VM", "VM Name", "Name") ?? -1;
        if (nameIndex < 0)
        {
            warnings = ["The VM worksheet has no recognized VM identity column; related worksheet joins are unavailable."];
            return null;
        }

        string[] stableHeaders = ["VM UUID", "Instance UUID", "Config.instanceUuid", "UUID", "MoRef", "Managed Object Reference"];
        int stableBaseIndex = FindColumnIndex(vmSheet.Headers, stableHeaders) ?? -1;
        string[] baseNames = vmSheet.Rows.Select(row => GetCell(row, nameIndex)).ToArray();
        var baseNameCounts = baseNames.Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var relatedRows = new List<(WorksheetData Sheet, int VmIndex, int StableIndex, int[] TargetColumns)>();
        foreach (WorksheetData sheet in workbook.Worksheets)
        {
            if (ReferenceEquals(sheet, vmSheet) || !IsVmRelatedWorksheet(sheet.Name)) continue;
            int vmIndex = FindColumnIndex(sheet.Headers, "VM", "VM Name") ?? -1;
            if (vmIndex < 0)
            {
                relationWarnings.Add($"{sheet.Name} has no VM identity column and was not joined to VM inventory.");
                continue;
            }
            int stableIndex = FindColumnIndex(sheet.Headers, stableHeaders) ?? -1;
            int[] targetColumns = Enumerable.Range(0, sheet.Headers.Count)
                .Where(index => index != vmIndex && index != stableIndex)
                .ToArray();
            relatedRows.Add((sheet, vmIndex, stableIndex, targetColumns));
        }

        var extraHeaders = new List<string>();
        foreach (var relation in relatedRows)
        {
            foreach (int targetColumn in relation.TargetColumns)
            {
                string header = $"{relation.Sheet.Name}.{relation.Sheet.Headers[targetColumn]}";
                if (!extraHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)) extraHeaders.Add(header);
            }
        }
        string[] headers = vmSheet.Headers.Concat(extraHeaders).ToArray();
        var relatedValues = new Dictionary<(int BaseRow, string Header), List<string>>();
        foreach (var relation in relatedRows)
        {
            foreach (WorksheetRow relatedRow in relation.Sheet.Rows)
            {
                string relatedName = GetCell(relatedRow, relation.VmIndex);
                if (string.IsNullOrWhiteSpace(relatedName)) continue;
                var matches = new List<int>();
                string? relatedStable = relation.StableIndex >= 0 ? GetCell(relatedRow, relation.StableIndex) : null;
                if (!string.IsNullOrWhiteSpace(relatedStable) && stableBaseIndex >= 0)
                {
                    matches.AddRange(vmSheet.Rows.Select((row, index) => (row, index))
                        .Where(item => string.Equals(GetCell(item.row, stableBaseIndex), relatedStable, StringComparison.OrdinalIgnoreCase))
                        .Select(item => item.index));
                }
                if (matches.Count == 0
                    && baseNameCounts.TryGetValue(relatedName, out int baseCount)
                    && baseCount == 1)
                {
                    int matchIndex = Array.FindIndex(baseNames, value => string.Equals(value, relatedName, StringComparison.OrdinalIgnoreCase));
                    if (matchIndex >= 0) matches.Add(matchIndex);
                }
                if (matches.Count != 1)
                {
                    if (matches.Count > 1 || (baseNameCounts.TryGetValue(relatedName, out int ambiguous) && ambiguous > 1))
                    {
                        relationWarnings.Add($"{relation.Sheet.Name} contains VM '{relatedName}' with an ambiguous identity; related values were not joined.");
                    }
                    continue;
                }

                int baseRowIndex = matches[0];
                foreach (int targetColumn in relation.TargetColumns)
                {
                    string value = GetCell(relatedRow, targetColumn);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    string header = $"{relation.Sheet.Name}.{relation.Sheet.Headers[targetColumn]}";
                    var key = (baseRowIndex, header);
                    if (!relatedValues.TryGetValue(key, out List<string>? values))
                    {
                        values = [];
                        relatedValues[key] = values;
                    }
                    if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
                }
            }
        }

        var rows = new List<WorksheetRow>(vmSheet.Rows.Count);
        for (int rowIndex = 0; rowIndex < vmSheet.Rows.Count; rowIndex++)
        {
            WorksheetRow source = vmSheet.Rows[rowIndex];
            var values = Enumerable.Repeat(string.Empty, headers.Length).ToArray();
            var displays = Enumerable.Repeat(string.Empty, headers.Length).ToArray();
            for (int i = 0; i < vmSheet.Headers.Count; i++)
            {
                values[i] = i < source.Values.Count ? source.Values[i] : string.Empty;
                displays[i] = i < source.DisplayValues.Count ? source.DisplayValues[i] : values[i];
            }
            for (int i = 0; i < extraHeaders.Count; i++)
            {
                if (relatedValues.TryGetValue((rowIndex, extraHeaders[i]), out List<string>? joined))
                {
                    values[vmSheet.Headers.Count + i] = string.Join("; ", joined);
                    displays[vmSheet.Headers.Count + i] = values[vmSheet.Headers.Count + i];
                }
            }
            rows.Add(new WorksheetRow { SourceRowNumber = source.SourceRowNumber, Values = values, DisplayValues = displays });
        }

        warnings = relationWarnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new WorksheetData { Name = "VM inventory (joined)", Headers = headers, RawHeaders = headers, Rows = rows };
    }

    public IReadOnlyList<WorksheetRow> ApplyFilters(
        WorksheetData sheet,
        IEnumerable<WorksheetRow> sourceRows,
        IReadOnlyList<InventoryFilter> filters,
        string? searchText = null)
    {
        IEnumerable<WorksheetRow> rows = sourceRows;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            string search = searchText.Trim();
            rows = rows.Where(row => row.Values.Any(value => value.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (InventoryFilter filter in filters)
        {
            int columnIndex = FindColumnIndex(sheet.Headers, filter.Field);
            if (columnIndex < 0) return [];
            rows = rows.Where(row => Matches(columnIndex < row.Values.Count ? row.Values[columnIndex] : string.Empty, filter));
        }
        return rows.ToArray();
    }

    public IReadOnlyDictionary<string, string> GetMetrics(WorkbookData workbook)
    {
        var entities = GetEntities(workbook);
        int Count(string kind) => entities.Count(entity => entity.Kind == kind);
        string Metric(string kind) => HasKindSheet(workbook, kind) ? Count(kind).ToString("N0", CultureInfo.CurrentCulture) : "N/A";

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Virtual machines"] = Metric("VM"),
            ["ESXi hosts"] = Metric("Host"),
            ["Clusters"] = Metric("Cluster"),
            ["Datastores"] = Metric("Datastore"),
            ["Networks"] = Metric("Network")
        };

        WorksheetData? vmSheet = FindSheet(workbook, "VM");
        if (vmSheet is not null)
        {
            int? powerColumn = FindColumnIndex(vmSheet.Headers, "Powerstate", "Power State", "Power");
            if (powerColumn >= 0)
            {
                result["Powered on"] = vmSheet.Rows.Count(row => IsPoweredOn(GetCell(row, powerColumn.Value))).ToString("N0", CultureInfo.CurrentCulture);
                result["Powered off"] = vmSheet.Rows.Count(row => IsPoweredOff(GetCell(row, powerColumn.Value))).ToString("N0", CultureInfo.CurrentCulture);
            }
            else
            {
                result["Powered on"] = "N/A";
                result["Powered off"] = "N/A";
            }
        }
        else
        {
            result["Powered on"] = "N/A";
            result["Powered off"] = "N/A";
        }

        (decimal? capacity, decimal? free) = GetDatastoreCapacity(workbook);
        result["Datastore capacity"] = capacity is null ? "N/A" : $"{capacity.Value:N1} GiB";
        result["Datastore free"] = free is null ? "N/A" : $"{free.Value:N1} GiB";
        result["Datastore used"] = capacity is null || free is null ? "N/A" : $"{Math.Max(0, capacity.Value - free.Value):N1} GiB";
        result["Datastore utilization"] = capacity is null || free is null || capacity.Value <= 0
            ? "N/A"
            : $"{Math.Clamp((capacity.Value - free.Value) / capacity.Value * 100m, 0m, 100m):N1}%";
        result["Capacity coverage"] = capacity is null || free is null
            ? "No supported capacity and free-space columns were found in the datastore worksheet."
            : "Derived from the vDatastore worksheet. Values are inventory metadata, not live performance.";

        result["Allocated vCPU"] = GetVmCpuAllocation(workbook) is decimal cpuCount
            ? $"{cpuCount:N0} vCPU"
            : "N/A";
        decimal? vmMemoryGiB = GetVmMemoryAllocation(workbook);
        result["VM memory allocation"] = vmMemoryGiB is null ? "N/A" : $"{vmMemoryGiB.Value:N1} GiB";
        decimal? provisionedDiskGiB = GetDiskProvisioning(workbook);
        result["Provisioned VM disks"] = provisionedDiskGiB is null ? "N/A" : $"{provisionedDiskGiB.Value:N1} GiB";
        WorksheetData? snapshotSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.StartsWith("vSnapshot", StringComparison.OrdinalIgnoreCase));
        result["Snapshots"] = snapshotSheet is null ? "N/A" : snapshotSheet.Rows.Count.ToString("N0", CultureInfo.CurrentCulture);
        return result;
    }

    public IReadOnlyList<string> GetFindings(WorkbookData workbook)
    {
        var findings = new List<string>();
        WorksheetData? datastoreSheet = FindSheet(workbook, "Datastore");
        int? capacityIndex = datastoreSheet is null ? null : FindColumnIndex(datastoreSheet.Headers, "Capacity", "Capacity MB", "Capacity GB", "Capacity MiB", "Capacity GiB");
        int? freeIndex = datastoreSheet is null ? null : FindColumnIndex(datastoreSheet.Headers, "Free", "Free MB", "Free GB", "Free MiB", "Free GiB");
        if (datastoreSheet is null || capacityIndex is null || freeIndex is null)
        {
            findings.Add("Datastore utilization could not be assessed because supported capacity and free-space fields are unavailable.");
        }
        else
        {
            foreach (WorksheetRow row in datastoreSheet.Rows)
            {
                if (!TryCapacityGiB(GetCell(row, capacityIndex.Value), datastoreSheet.Headers[capacityIndex.Value], out decimal capacity)
                    || !TryCapacityGiB(GetCell(row, freeIndex.Value), datastoreSheet.Headers[freeIndex.Value], out decimal free)
                    || capacity <= 0)
                {
                    continue;
                }
                decimal utilization = Math.Clamp((capacity - free) / capacity * 100m, 0m, 100m);
                if (utilization >= 80m)
                {
                    string name = GetCell(row, FindColumnIndex(datastoreSheet.Headers, "Datastore", "Name") ?? 0);
                    findings.Add($"Datastore '{name}' is {utilization:N1}% used in this snapshot (default review threshold: 80%; source: {datastoreSheet.Name}, row {row.SourceRowNumber}).");
                }
            }
            if (findings.Count == 0)
            {
                findings.Add("No datastore at or above the default 80% utilization review threshold was found in the available capacity rows.");
            }
        }

        WorksheetData? toolsSheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.StartsWith("vTools", StringComparison.OrdinalIgnoreCase));
        if (toolsSheet is not null)
        {
            int[] statusColumns = toolsSheet.Headers.Select((header, index) => (header, index))
                .Where(item => item.header.Contains("status", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index)
                .ToArray();
            foreach (WorksheetRow row in toolsSheet.Rows)
            {
                string[] problemValues = statusColumns.Select(index => GetCell(row, index))
                    .Where(value => value.Contains("needupgrade", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("out of date", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                        || value.Contains("not running", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (problemValues.Length > 0)
                {
                    int vmIndex = FindColumnIndex(toolsSheet.Headers, "VM", "VM Name") ?? 0;
                    findings.Add($"VM '{GetCell(row, vmIndex)}' has a VMware Tools status to review ({string.Join(", ", problemValues.Distinct(StringComparer.OrdinalIgnoreCase))}; source: {toolsSheet.Name}, row {row.SourceRowNumber}).");
                }
            }
        }
        return findings;
    }

    public IReadOnlyList<EntityChange> Compare(WorkbookData baseline, WorkbookData target, bool sameEnvironment)
    {
        IReadOnlyList<InventoryEntity> baselineEntities = GetEntities(baseline);
        IReadOnlyList<InventoryEntity> targetEntities = GetEntities(target);
        var changes = new List<EntityChange>();

        foreach (string kind in CoreKinds)
        {
            bool baselineCovered = HasKindSheet(baseline, kind);
            bool targetCovered = HasKindSheet(target, kind);
            if (!baselineCovered || !targetCovered)
            {
                if (baselineCovered != targetCovered)
                {
                    changes.Add(new EntityChange(kind, string.Empty, "Coverage unavailable", "Low", []));
                }
                continue;
            }

            var baseKind = baselineEntities.Where(entity => entity.Kind == kind).ToArray();
            var targetKind = targetEntities.Where(entity => entity.Kind == kind).ToArray();
            var ambiguousBase = baseKind.GroupBy(entity => entity.Identity, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ambiguousTarget = targetKind.GroupBy(entity => entity.Identity, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseMap = baseKind.Where(entity => !ambiguousBase.Contains(entity.Identity))
                .ToDictionary(entity => entity.Identity, StringComparer.OrdinalIgnoreCase);
            var targetMap = targetKind.Where(entity => !ambiguousTarget.Contains(entity.Identity))
                .ToDictionary(entity => entity.Identity, StringComparer.OrdinalIgnoreCase);

            foreach (InventoryEntity entity in baseKind.Where(item => ambiguousBase.Contains(item.Identity)))
            {
                changes.Add(new EntityChange(kind, entity.Name, "Ambiguous identity", "Low", []));
            }
            foreach (InventoryEntity entity in targetKind.Where(item => ambiguousTarget.Contains(item.Identity)))
            {
                changes.Add(new EntityChange(kind, entity.Name, "Ambiguous identity", "Low", []));
            }

            foreach ((string identity, InventoryEntity entity) in targetMap)
            {
                if (!baseMap.TryGetValue(identity, out InventoryEntity? oldEntity))
                {
                    changes.Add(new EntityChange(kind, entity.Name, sameEnvironment ? "Added" : "Observed in target", entity.IdentityConfidence, []));
                    continue;
                }

                IReadOnlyList<FieldChange> fieldChanges = CompareFields(oldEntity.Fields, entity.Fields);
                if (fieldChanges.Count > 0)
                {
                    string confidence = oldEntity.IdentityConfidence == "High" && entity.IdentityConfidence == "High" ? "High" : "Medium";
                    changes.Add(new EntityChange(kind, entity.Name, "Modified", confidence, fieldChanges));
                }
            }

            foreach ((string identity, InventoryEntity entity) in baseMap)
            {
                if (!targetMap.ContainsKey(identity))
                {
                    changes.Add(new EntityChange(kind, entity.Name, sameEnvironment ? "Removed" : "Not observed in target", entity.IdentityConfidence, []));
                }
            }
        }
        return changes.OrderBy(change => change.Kind).ThenBy(change => change.ChangeType).ThenBy(change => change.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? GetKind(string worksheetName)
    {
        string name = worksheetName.Trim();
        if (name.StartsWith("vInfo", StringComparison.OrdinalIgnoreCase) || name.Contains("VM Info", StringComparison.OrdinalIgnoreCase)) return "VM";
        if (name.StartsWith("vHost", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) return "Host";
        if (name.StartsWith("vCluster", StringComparison.OrdinalIgnoreCase) || name.Equals("Cluster", StringComparison.OrdinalIgnoreCase)) return "Cluster";
        if (name.StartsWith("vDatastore", StringComparison.OrdinalIgnoreCase) || name.Equals("Datastore", StringComparison.OrdinalIgnoreCase)) return "Datastore";
        if (name.StartsWith("vNetwork", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vPort", StringComparison.OrdinalIgnoreCase) || name.StartsWith("vSwitch", StringComparison.OrdinalIgnoreCase)) return "Network";
        return null;
    }

    public static WorksheetData? FindSheet(WorkbookData workbook, string kind) =>
        workbook.Worksheets.FirstOrDefault(sheet => string.Equals(GetKind(sheet.Name), kind, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, string> ToFields(WorksheetData sheet, WorksheetRow row)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sheet.Headers.Count; i++)
        {
            fields[sheet.Headers[i]] = i < row.Values.Count ? row.Values[i] : string.Empty;
        }
        return fields;
    }

    private static bool HasKindSheet(WorkbookData workbook, string kind) =>
        workbook.Worksheets.Any(sheet => string.Equals(GetKind(sheet.Name), kind, StringComparison.OrdinalIgnoreCase));

    private static bool IsVmRelatedWorksheet(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.StartsWith("vdisk", StringComparison.Ordinal)
            || lower.StartsWith("vpartition", StringComparison.Ordinal)
            || lower.StartsWith("vnic", StringComparison.Ordinal)
            || lower.StartsWith("vsnapshot", StringComparison.Ordinal)
            || lower.StartsWith("vcd", StringComparison.Ordinal)
            || lower.StartsWith("vtools", StringComparison.Ordinal)
            || lower.StartsWith("vcpu", StringComparison.Ordinal)
            || lower.StartsWith("vmemory", StringComparison.Ordinal);
    }

    private static string GetName(string kind, IReadOnlyDictionary<string, string> fields)
    {
        string[] candidates = kind switch
        {
            "VM" => ["VM", "Name", "VM Name"],
            "Host" => ["Host", "Name", "ESXi Host"],
            "Cluster" => ["Cluster", "Name"],
            "Datastore" => ["Datastore", "Name"],
            "Network" => ["Network", "Portgroup", "Port Group", "Name", "Switch"],
            _ => ["Name"]
        };
        return FirstValue(fields, candidates);
    }

    private static (string Identity, string Confidence) GetIdentity(string kind, IReadOnlyDictionary<string, string> fields, string name)
    {
        string[] stableKeys = ["MoRef", "Managed Object Reference", "VM UUID", "Instance UUID", "Config.instanceUuid", "UUID", "Host UUID", "Datastore UUID"];
        string stable = FirstValue(fields, stableKeys);
        if (!string.IsNullOrWhiteSpace(stable)) return ($"{kind}:id:{stable.Trim()}", "High");
        return ($"{kind}:name:{name.Trim()}", "Low");
    }

    private static string FirstValue(IReadOnlyDictionary<string, string> fields, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            KeyValuePair<string, string>? field = fields.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (field is { } pair && !string.IsNullOrWhiteSpace(pair.Value)) return pair.Value.Trim();
        }
        return string.Empty;
    }

    private static IReadOnlyList<FieldChange> CompareFields(IReadOnlyDictionary<string, string> baseline, IReadOnlyDictionary<string, string> target)
    {
        var changes = new List<FieldChange>();
        foreach (string key in baseline.Keys.Union(target.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            baseline.TryGetValue(key, out string? oldValue);
            target.TryGetValue(key, out string? newValue);
            if (!string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(key, oldValue ?? string.Empty, newValue ?? string.Empty));
            }
        }
        return changes;
    }

    private static string GetCell(WorksheetRow row, int index) => index < row.Values.Count ? row.Values[index] : string.Empty;

    private static bool IsPoweredOn(string value) => value.Contains("poweredOn", StringComparison.OrdinalIgnoreCase) || value.Equals("On", StringComparison.OrdinalIgnoreCase);
    private static bool IsPoweredOff(string value) => value.Contains("poweredOff", StringComparison.OrdinalIgnoreCase) || value.Equals("Off", StringComparison.OrdinalIgnoreCase);

    private static (decimal? Capacity, decimal? Free) GetDatastoreCapacity(WorkbookData workbook)
    {
        WorksheetData? sheet = FindSheet(workbook, "Datastore");
        if (sheet is null) return (null, null);
        int? capacityColumn = FindColumnIndex(sheet.Headers, "Capacity", "Capacity MB", "Capacity GB", "Capacity MiB", "Capacity GiB");
        int? freeColumn = FindColumnIndex(sheet.Headers, "Free", "Free MB", "Free GB", "Free MiB", "Free GiB");
        if (capacityColumn is null || freeColumn is null) return (null, null);

        decimal capacity = 0;
        decimal free = 0;
        bool hasCapacity = false;
        bool hasFree = false;
        foreach (WorksheetRow row in sheet.Rows)
        {
            if (TryCapacityGiB(GetCell(row, capacityColumn.Value), sheet.Headers[capacityColumn.Value], out decimal rowCapacity))
            {
                capacity += rowCapacity;
                hasCapacity = true;
            }
            if (TryCapacityGiB(GetCell(row, freeColumn.Value), sheet.Headers[freeColumn.Value], out decimal rowFree))
            {
                free += rowFree;
                hasFree = true;
            }
        }
        return (hasCapacity ? capacity : null, hasFree ? free : null);
    }

    private static decimal? GetVmCpuAllocation(WorkbookData workbook)
    {
        WorksheetData? sheet = FindSheet(workbook, "VM");
        if (sheet is null) return null;
        int? cpuColumn = FindColumnIndex(sheet.Headers, "CPUs", "vCPU", "vCPUs");
        if (cpuColumn is null) return null;
        decimal total = 0;
        bool found = false;
        foreach (WorksheetRow row in sheet.Rows)
        {
            if (decimal.TryParse(GetCell(row, cpuColumn.Value), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value) && value >= 0)
            {
                total += value;
                found = true;
            }
        }
        return found ? total : null;
    }

    private static decimal? GetVmMemoryAllocation(WorkbookData workbook)
    {
        WorksheetData? sheet = FindSheet(workbook, "VM");
        if (sheet is null) return null;
        int? memoryColumn = FindColumnIndex(sheet.Headers, "Memory MB", "Memory (MB)", "Memory MiB");
        if (memoryColumn is null) return null;
        return SumAsGiB(sheet, memoryColumn.Value);
    }

    private static decimal? GetDiskProvisioning(WorkbookData workbook)
    {
        WorksheetData? sheet = workbook.Worksheets.FirstOrDefault(item => item.Name.StartsWith("vDisk", StringComparison.OrdinalIgnoreCase));
        if (sheet is null) return null;
        int? capacityColumn = FindColumnIndex(sheet.Headers, "Capacity MB", "Capacity (MB)", "Capacity MiB", "Capacity (MiB)", "Capacity GB", "Capacity (GB)", "Capacity GiB", "Capacity (GiB)");
        return capacityColumn is null ? null : SumAsGiB(sheet, capacityColumn.Value);
    }

    private static decimal? SumAsGiB(WorksheetData sheet, int columnIndex)
    {
        string header = sheet.Headers[columnIndex];
        decimal total = 0;
        bool found = false;
        foreach (WorksheetRow row in sheet.Rows)
        {
            if (TryCapacityGiB(GetCell(row, columnIndex), header, out decimal value))
            {
                total += value;
                found = true;
            }
        }
        return found ? total : null;
    }

    private static bool TryCapacityGiB(string value, string header, out decimal result)
    {
        result = 0;
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)) return false;
        string unit = header.ToLowerInvariant();
        result = unit.Contains("tb", StringComparison.Ordinal) ? parsed * 1024m :
            unit.Contains("mb", StringComparison.Ordinal) || unit.Contains("mib", StringComparison.Ordinal) ? parsed / 1024m :
            unit.Contains("bytes", StringComparison.Ordinal) ? parsed / 1073741824m :
            unit.Contains("kb", StringComparison.Ordinal) ? parsed / 1048576m :
            parsed;
        return result >= 0;
    }

    private static int? FindColumnIndex(IReadOnlyList<string> headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (candidates.Any(candidate => string.Equals(headers[i], candidate, StringComparison.OrdinalIgnoreCase)
                || headers[i].StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)
                || headers[i].StartsWith(candidate + " (", StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }
        return null;
    }

    private static int FindColumnIndex(IReadOnlyList<string> headers, string field)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (string.Equals(headers[i], field, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private static bool Matches(string actual, InventoryFilter filter)
    {
        string expected = filter.Value ?? string.Empty;
        return filter.Operator switch
        {
            "Contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "Not contains" => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "Equals" => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "Starts with" => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "Is empty" => string.IsNullOrWhiteSpace(actual),
            "Is not empty" => !string.IsNullOrWhiteSpace(actual),
            "Greater than" => TryCompareNumber(actual, expected, out int greater) && greater > 0,
            "Less than" => TryCompareNumber(actual, expected, out int less) && less < 0,
            _ => true
        };
    }

    private static bool TryCompareNumber(string actual, string expected, out int compare)
    {
        compare = 0;
        return decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal actualNumber)
            && decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal expectedNumber)
            && (compare = actualNumber.CompareTo(expectedNumber)) == compare;
    }
}
