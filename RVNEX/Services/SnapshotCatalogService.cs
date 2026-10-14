using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RVNEX.Services;

internal sealed class SnapshotCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly WorkbookReader _reader;
    private readonly string _catalogPath;
    private readonly string _legacyReportsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SnapshotCatalog? _catalog;
    private string? _loadWarning;

    public SnapshotCatalogService(WorkbookReader reader)
        : this(
            reader,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RVNEX", "catalog.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RVNEX Reports"))
    {
    }

    internal SnapshotCatalogService(WorkbookReader reader, string catalogPath, string legacyReportsDirectory)
    {
        _reader = reader;
        _catalogPath = Path.GetFullPath(catalogPath);
        _legacyReportsDirectory = Path.GetFullPath(legacyReportsDirectory);
    }

    public string? LoadWarning => _loadWarning;

    public async Task<IReadOnlyList<SnapshotRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is null)
            {
                _catalog = await ReadCatalogAsync(cancellationToken);
                if (_catalog.Snapshots.Count == 0)
                {
                    await DiscoverExistingReportsAsync(cancellationToken);
                }
                UpdateFileAvailability();
            }
            return _catalog.Snapshots.OrderByDescending(record => record.CollectedUtc ?? record.ImportedUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(SnapshotRecord? Record, bool Duplicate)> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        WorkbookData workbook = await _reader.ReadAsync(path, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            SnapshotRecord? duplicate = _catalog!.Snapshots.FirstOrDefault(record =>
                record.Sha256.Equals(workbook.Sha256, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                return (duplicate, true);
            }

            SnapshotRecord record = CreateRecord(workbook, sourceAddress: null, rvToolsVersion: null, collectedUtc: null);
            _catalog.Snapshots.Add(record);
            await SaveCatalogAsync(cancellationToken);
            return (record, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SnapshotRecord> RegisterCollectionAsync(
        string path,
        string sourceAddress,
        string? rvToolsVersion,
        CancellationToken cancellationToken = default)
    {
        WorkbookData workbook = await _reader.ReadAsync(path, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            SnapshotRecord record = CreateRecord(workbook, sourceAddress, rvToolsVersion, DateTime.UtcNow);
            record.Note = "Collected by RVTools and validated by RVNEX.";
            _catalog!.Snapshots.Insert(0, record);
            await SaveCatalogAsync(cancellationToken);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SnapshotRecord> RegisterCollectionAsync(
        string path,
        ConnectionProfile profile,
        CollectionAttemptResult result,
        string? batchRunId = null,
        CancellationToken cancellationToken = default)
    {
        WorkbookData workbook = await _reader.ReadAsync(path, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            SnapshotRecord record = CreateRecord(workbook, profile.Address, result.RvToolsVersion, result.EndedUtc ?? DateTime.UtcNow);
            record.Name = $"{profile.Name} - {Path.GetFileNameWithoutExtension(path)}";
            record.ConnectionProfileId = profile.Id;
            record.EnvironmentIdentity = $"{profile.Group ?? profile.Name} / {profile.Address}";
            record.CollectionRunId = result.RunId;
            record.BatchRunId = batchRunId;
            record.CollectionStartedUtc = result.StartedUtc;
            record.CollectionEndedUtc = result.EndedUtc;
            record.RvToolsExecutablePath = result.ExecutablePath;
            record.ProcessExitCode = result.ExitCode;
            record.WorkbookValidationOutcome = "XLSX structure and workbook contents parsed successfully";
            record.SourceKind = "Collected";
            record.Note = "Collected by RVTools and validated by RVNEX.";
            _catalog!.Snapshots.Insert(0, record);
            await SaveCatalogAsync(cancellationToken);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            SnapshotRecord? record = _catalog!.Snapshots.FirstOrDefault(item => item.Id == id);
            if (record is null) throw new InvalidOperationException("The selected report is no longer in the catalog.");
            record.Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(record.WorkbookPath) : name.Trim();
            await SaveCatalogAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            _catalog!.Snapshots.RemoveAll(record => record.Id == id);
            await SaveCatalogAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(cancellationToken);
            UpdateFileAvailability();
            await SaveCatalogAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            _catalog = await ReadCatalogAsync(cancellationToken);
            UpdateFileAvailability();
        }
    }

    private async Task<SnapshotCatalog> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath)) return new SnapshotCatalog();
        try
        {
            await using var stream = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            SnapshotCatalog? catalog = await JsonSerializer.DeserializeAsync<SnapshotCatalog>(stream, JsonOptions, cancellationToken);
            if (catalog is null || catalog.SchemaVersion != 1 || catalog.Snapshots is null)
            {
                _loadWarning = "The report catalog has an unsupported or empty format. The original file has been preserved.";
                return new SnapshotCatalog();
            }
            return catalog;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _loadWarning = "The report catalog could not be read. Its original file has been preserved; the catalog will be recovered when the next change is saved.";
            return new SnapshotCatalog();
        }
    }

    private async Task DiscoverExistingReportsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_legacyReportsDirectory)) return;
        foreach (string path in Directory.EnumerateFiles(_legacyReportsDirectory, "*.xlsx", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                WorkbookData workbook = await _reader.ReadAsync(path, cancellationToken);
                _catalog!.Snapshots.Add(WithNote(CreateRecord(workbook, null, null, null), "Discovered existing RVNEX report."));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
            {
                // Invalid legacy files remain untouched and can still be imported manually for an error message.
            }
        }
        if (_catalog!.Snapshots.Count > 0)
        {
            await SaveCatalogAsync(cancellationToken);
        }
    }

    private static SnapshotRecord WithNote(SnapshotRecord record, string note)
    {
        record.Note = note;
        return record;
    }

    private SnapshotRecord CreateRecord(WorkbookData workbook, string? sourceAddress, string? rvToolsVersion, DateTime? collectedUtc)
    {
        int Count(params string[] prefixes)
        {
            WorksheetData? sheet = workbook.Worksheets.FirstOrDefault(item => prefixes.Any(prefix => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            return sheet is null ? 0 : CountDataRows(sheet);
        }

        return new SnapshotRecord
        {
            Name = Path.GetFileNameWithoutExtension(workbook.SourcePath),
            WorkbookPath = workbook.SourcePath,
            Sha256 = workbook.Sha256,
            SourceAddress = sourceAddress,
            RvToolsVersion = rvToolsVersion,
            ImportedUtc = DateTime.UtcNow,
            CollectedUtc = collectedUtc,
            VmCount = Count("vInfo", "VM Info"),
            HostCount = Count("vHost", "Host"),
            ClusterCount = Count("vCluster", "Cluster"),
            DatastoreCount = Count("vDatastore", "Datastore"),
            NetworkCount = workbook.Worksheets.Any(item => item.Name.StartsWith("vNetwork", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Name, "Network", StringComparison.OrdinalIgnoreCase))
                ? Count("vNetwork", "Network")
                : Count("vPort", "vSwitch"),
            WorkbookValid = workbook.Worksheets.Count > 0,
            FileAvailable = File.Exists(workbook.SourcePath),
            SourceKind = collectedUtc is null ? "Imported" : "Collected",
            Note = string.Join(" ", workbook.Warnings)
        };
    }

    private static int CountDataRows(WorksheetData sheet)
    {
        if (sheet.Rows.Count == 0) return 0;
        string[] identityHeaders = ["VM", "Name", "Host", "Cluster", "Datacenter", "Datastore", "Network", "Portgroup"];
        int keyIndex = -1;
        for (int i = 0; i < sheet.Headers.Count; i++)
        {
            if (identityHeaders.Any(header => string.Equals(sheet.Headers[i], header, StringComparison.OrdinalIgnoreCase)))
            {
                keyIndex = i;
                break;
            }
        }
        return keyIndex < 0 ? sheet.Rows.Count : sheet.Rows.Count(row => keyIndex < row.Values.Count && !string.IsNullOrWhiteSpace(row.Values[keyIndex]));
    }

    private async Task SaveCatalogAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        string tempPath = _catalogPath + ".tmp";
        string backupPath = _catalogPath + ".bak";
        if (File.Exists(_catalogPath))
        {
            File.Copy(_catalogPath, backupPath, overwrite: true);
        }

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, _catalog, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(_catalogPath))
            {
                File.Replace(tempPath, _catalogPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _catalogPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void UpdateFileAvailability()
    {
        foreach (SnapshotRecord record in _catalog!.Snapshots)
        {
            record.FileAvailable = File.Exists(record.WorkbookPath);
        }
    }
}
