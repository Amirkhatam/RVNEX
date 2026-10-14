using RVNEX.Models;
using RVNEX.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RVNEX;

internal sealed class WorkspaceViewModel
{
    private readonly WorkbookReader _reader = new();
    private readonly SnapshotCatalogService _catalog;
    private readonly Dictionary<string, WorkbookData> _loaded = new();

    public WorkspaceViewModel()
    {
        _catalog = new SnapshotCatalogService(_reader);
    }

    public SnapshotCatalogService Catalog => _catalog;
    public InventoryAnalytics Analytics { get; } = new();
    public string? CatalogWarning => _catalog.LoadWarning;

    public Task<IReadOnlyList<SnapshotRecord>> LoadSnapshotsAsync(CancellationToken cancellationToken = default) =>
        _catalog.LoadAsync(cancellationToken);

    public async Task<WorkbookData> LoadWorkbookAsync(SnapshotRecord record, CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(record.Sha256, out WorkbookData? cached))
        {
            return cached;
        }

        WorkbookData workbook = await _reader.ReadAsync(record.WorkbookPath, cancellationToken);
        _loaded[record.Sha256] = workbook;
        return workbook;
    }

    public async Task<(SnapshotRecord? Record, bool Duplicate)> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _catalog.ImportAsync(path, cancellationToken);
        if (result.Record is not null && !result.Duplicate)
        {
            _loaded[result.Record.Sha256] = await _reader.ReadAsync(result.Record.WorkbookPath, cancellationToken);
        }
        return result;
    }

    public async Task<SnapshotRecord> RegisterCollectionAsync(
        string path,
        string address,
        string? version,
        CancellationToken cancellationToken = default)
    {
        SnapshotRecord record = await _catalog.RegisterCollectionAsync(path, address, version, cancellationToken);
        _loaded[record.Sha256] = await _reader.ReadAsync(record.WorkbookPath, cancellationToken);
        return record;
    }

    public async Task<SnapshotRecord> RegisterCollectionAsync(
        string path,
        RVNEX.Models.ConnectionProfile profile,
        CollectionAttemptResult result,
        string? batchRunId = null,
        CancellationToken cancellationToken = default)
    {
        SnapshotRecord record = await _catalog.RegisterCollectionAsync(path, profile, result, batchRunId, cancellationToken);
        _loaded[record.Sha256] = await _reader.ReadAsync(record.WorkbookPath, cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<SnapshotRecord>> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        await _catalog.RenameAsync(id, name, cancellationToken);
        return await _catalog.LoadAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SnapshotRecord>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _catalog.RemoveAsync(id, cancellationToken);
        return await _catalog.LoadAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SnapshotRecord>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.RefreshAsync(cancellationToken);
        return await _catalog.LoadAsync(cancellationToken);
    }
}
