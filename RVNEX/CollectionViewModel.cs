using RVNEX.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RVNEX;

public sealed class CollectionViewModel
{
    private readonly RvToolsCollector _collector;

    public CollectionViewModel() : this(new RvToolsCollector()) { }
    internal CollectionViewModel(RvToolsCollector collector) => _collector = collector;

    public async Task<CollectionAttemptResult> StartCollectionAsync(
        ConnectionProfile profile,
        string password,
        IProgress<CollectionProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? collectionTimeout = null,
        bool retainReport = true)
    {
        if (profile is null) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "Select a connection profile.");
        if (string.IsNullOrWhiteSpace(profile.Name)) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "The selected profile has no name.");
        if (!Services.ConnectionProfileService.IsValidAddress(profile.Address)) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "Enter a host name or IP address without a scheme or path.");
        if (profile.Port is < 1 or > 65535) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "The profile port must be between 1 and 65535.");
        if (profile.Port != 443)
            return CollectionAttemptResult.Failure(CollectionStatus.UnsupportedPort, "The verified RVTools command syntax does not document a custom server-port argument. RVNEX currently requires port 443.");
        if (string.IsNullOrWhiteSpace(profile.Username)) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "Enter a username in the connection profile.");
        if (string.IsNullOrEmpty(password)) return CollectionAttemptResult.Failure(CollectionStatus.ValidationError, "A password is required for this collection.");

        return await _collector.CollectAsync(profile, password, progress, cancellationToken, collectionTimeout, retainReport);
    }
}

public sealed record CollectionProgress(string Message);

public enum CollectionStatus
{
    ValidationError,
    MissingRvTools,
    UnsupportedPort,
    FailedAuthentication,
    FailedConnection,
    FailedTlsOrCertificate,
    UnresolvedTlsFailure,
    FailedExecution,
    FailedWorkbookValidation,
    TimedOut,
    Cancelled,
    SkippedMissingCredential,
    SkippedDisabled,
    Succeeded
}

public sealed record CollectionAttemptResult(
    CollectionStatus Status,
    string Message,
    string? ReportPath = null,
    string? RvToolsVersion = null,
    string RunId = "",
    DateTime? StartedUtc = null,
    DateTime? EndedUtc = null,
    int? ExitCode = null,
    string? ExecutablePath = null,
    string? Diagnostic = null)
{
    public bool IsSuccess => Status == CollectionStatus.Succeeded;

    public static CollectionAttemptResult Failure(CollectionStatus status, string message) =>
        new(status, message, StartedUtc: DateTime.UtcNow, EndedUtc: DateTime.UtcNow, Diagnostic: message);
}
