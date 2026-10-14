using System;
using System.Collections.Generic;

namespace RVNEX.Models;

public sealed class WorkbookData
{
    public required string SourcePath { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<WorksheetData> Worksheets { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class WorksheetData
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Headers { get; init; }
    public required IReadOnlyList<string> RawHeaders { get; init; }
    public required IReadOnlyList<WorksheetRow> Rows { get; init; }
}

public sealed class WorksheetRow
{
    public required int SourceRowNumber { get; init; }
    /// <summary>Unmodified cached cell values as stored in the workbook.</summary>
    public required IReadOnlyList<string> Values { get; init; }
    /// <summary>Display-friendly values, including dates when a date style is known.</summary>
    public IReadOnlyList<string> DisplayValues { get; init; } = Array.Empty<string>();
}

public sealed class SnapshotRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string WorkbookPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string? SourceAddress { get; set; }
    public string? RvToolsVersion { get; set; }
    public string? ConnectionProfileId { get; set; }
    public string? EnvironmentIdentity { get; set; }
    public string? CollectionRunId { get; set; }
    public string? BatchRunId { get; set; }
    public DateTime? CollectionStartedUtc { get; set; }
    public DateTime? CollectionEndedUtc { get; set; }
    public string? RvToolsExecutablePath { get; set; }
    public int? ProcessExitCode { get; set; }
    public string? WorkbookValidationOutcome { get; set; }
    public string SourceKind { get; set; } = "Imported";
    public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CollectedUtc { get; set; }
    public int VmCount { get; set; }
    public int HostCount { get; set; }
    public int ClusterCount { get; set; }
    public int DatastoreCount { get; set; }
    public int NetworkCount { get; set; }
    public bool WorkbookValid { get; set; }
    public bool FileAvailable { get; set; }
    public string? Note { get; set; }
}

public sealed class SnapshotCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<SnapshotRecord> Snapshots { get; set; } = [];
}

public sealed record InventoryEntity(
    string Kind,
    string Identity,
    string IdentityConfidence,
    string Name,
    string Worksheet,
    int SourceRowNumber,
    IReadOnlyDictionary<string, string> Fields);

public sealed record InventoryFilter(string Field, string Operator, string Value);

public sealed record FieldChange(string Field, string BaselineValue, string TargetValue);

public sealed record EntityChange(string Kind, string Name, string ChangeType, string Confidence, IReadOnlyList<FieldChange> Fields);
