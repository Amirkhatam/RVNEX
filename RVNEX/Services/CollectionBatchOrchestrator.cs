using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RVNEX.Services;

internal sealed record BatchProgressUpdate(ConnectionProfile Profile, string State, string Details, DateTime? StartedUtc = null, DateTime? EndedUtc = null);
internal sealed record BatchTargetOutcome(string ProfileId, string State, CollectionAttemptResult Result);
internal sealed record BatchRunResult(string RunId, int Configured, int Enabled, int Eligible, IReadOnlyList<BatchTargetOutcome> Targets)
{
    public IReadOnlyList<string> FailedProfileIds => Targets.Where(t => t.State is "Failed" or "Timed out").Select(t => t.ProfileId).ToArray();
}

/// <summary>Sequential, non-UI coordinator for Get'em All. It never prompts for missing credentials.</summary>
internal sealed class CollectionBatchOrchestrator
{
    private readonly ConnectionProfileService _profiles;
    private readonly Func<ConnectionProfile, Task<bool>> _hasCredential;
    private readonly Func<ConnectionProfile, Task<string?>> _getCredential;
    private readonly Func<ConnectionProfile, bool> _hasExecutable;
    private readonly Func<ConnectionProfile, string, IProgress<CollectionProgress>, CancellationToken, Task<CollectionAttemptResult>> _collect;
    private readonly Func<ConnectionProfile, CollectionAttemptResult, string, CancellationToken, Task<CollectionAttemptResult>> _finalize;
    private readonly int _maximumRetries;

    public CollectionBatchOrchestrator(
        ConnectionProfileService profiles,
        Func<ConnectionProfile, Task<bool>> hasCredential,
        Func<ConnectionProfile, Task<string?>> getCredential,
        Func<ConnectionProfile, bool> hasExecutable,
        Func<ConnectionProfile, string, IProgress<CollectionProgress>, CancellationToken, Task<CollectionAttemptResult>> collect,
        Func<ConnectionProfile, CollectionAttemptResult, string, CancellationToken, Task<CollectionAttemptResult>> finalize,
        int maximumRetries)
    {
        _profiles = profiles;
        _hasCredential = hasCredential;
        _getCredential = getCredential;
        _hasExecutable = hasExecutable;
        _collect = collect;
        _finalize = finalize;
        _maximumRetries = Math.Clamp(maximumRetries, 0, 3);
    }

    public async Task<BatchRunResult> RunAsync(
        IReadOnlyList<ConnectionProfile> targets,
        string runId,
        Action<BatchProgressUpdate> report,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<BatchTargetOutcome>(targets.Count);
        int eligible = 0;
        int enabled = targets.Count(p => p.Enabled);
        var queued = new List<ConnectionProfile>();

        foreach (ConnectionProfile profile in targets)
        {
            if (!profile.Enabled)
            {
                var skipped = CollectionAttemptResult.Failure(CollectionStatus.SkippedDisabled, "Profile is disabled.");
                bool recorded = await RecordAttemptAsync(profile, skipped, runId);
                AddOutcome(profile, "Skipped", skipped, "Profile is disabled." + HistoryWarning(recorded));
                continue;
            }

            bool hasCredential;
            try { hasCredential = await _hasCredential(profile); }
            catch { hasCredential = false; }
            if (!hasCredential)
            {
                var skipped = CollectionAttemptResult.Failure(CollectionStatus.SkippedMissingCredential, "No saved credential. Manual collection can request a password; batch collection never prompts.");
                bool recorded = await RecordAttemptAsync(profile, skipped, runId);
                AddOutcome(profile, "Skipped", skipped, "No saved credential. Get'em All does not prompt for passwords." + HistoryWarning(recorded));
                continue;
            }

            bool hasExecutable;
            try { hasExecutable = _hasExecutable(profile); }
            catch { hasExecutable = false; }
            if (!hasExecutable)
            {
                var failed = CollectionAttemptResult.Failure(CollectionStatus.MissingRvTools, "The profile's RVTools executable is missing or unsupported.");
                bool recorded = await RecordAttemptAsync(profile, failed, runId);
                AddOutcome(profile, "Failed", failed, failed.Message + HistoryWarning(recorded));
                continue;
            }
            if (profile.Port != 443)
            {
                var failed = CollectionAttemptResult.Failure(CollectionStatus.UnsupportedPort, "The verified RVTools command syntax does not document a custom server-port argument. RVNEX currently requires port 443.");
                bool recorded = await RecordAttemptAsync(profile, failed, runId);
                AddOutcome(profile, "Failed", failed, failed.Message + HistoryWarning(recorded));
                continue;
            }
            queued.Add(profile);
            eligible++;
            report(new BatchProgressUpdate(profile, "Queued", "Ready for sequential collection."));
        }

        foreach (ConnectionProfile profile in queued)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var cancelled = CollectionAttemptResult.Failure(CollectionStatus.Cancelled, "Cancelled before this target started.");
                await RecordAttemptAsync(profile, cancelled, runId);
                AddOutcome(profile, "Cancelled", cancelled, cancelled.Message);
                continue;
            }

            report(new BatchProgressUpdate(profile, "Validating", "Retrieving this profile's saved credential."));
            string? password;
            try { password = await _getCredential(profile); }
            catch (Exception ex)
            {
                var failed = CollectionAttemptResult.Failure(CollectionStatus.FailedExecution, "Windows Credential Manager could not retrieve this profile credential.") with { Diagnostic = $"Credential retrieval error ({ex.GetType().Name})." };
                bool recorded = await RecordAttemptAsync(profile, failed, runId);
                AddOutcome(profile, "Failed", failed, failed.Message + HistoryWarning(recorded));
                continue;
            }
            if (string.IsNullOrEmpty(password))
            {
                try { await _profiles.MarkCredentialSavedAsync(profile.Id, false); }
                catch { }
                var skipped = CollectionAttemptResult.Failure(CollectionStatus.SkippedMissingCredential, "Credential is no longer available in Windows Credential Manager.");
                bool recorded = await RecordAttemptAsync(profile, skipped, runId);
                AddOutcome(profile, "Skipped", skipped, skipped.Message + HistoryWarning(recorded));
                continue;
            }

            DateTime started = DateTime.UtcNow;
            report(new BatchProgressUpdate(profile, "Running", "RVTools started; its internal progress is not observable.", started));
            var progress = new Progress<CollectionProgress>(update => report(new BatchProgressUpdate(profile, "Running", update.Message, started)));
            CollectionAttemptResult result;
            int retries = 0;
            bool historyWriteFailed = false;
            while (true)
            {
                try
                {
                    result = await _collect(profile, password, progress, cancellationToken);
                    result = await _finalize(profile, result, runId, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    result = CollectionAttemptResult.Failure(CollectionStatus.Cancelled, "Collection was cancelled.");
                }
                catch (Exception ex)
                {
                    result = CollectionAttemptResult.Failure(CollectionStatus.FailedExecution, "RVNEX failed while processing this target.") with { Diagnostic = $"Unexpected collection error ({ex.GetType().Name})." };
                }
                if (!await RecordAttemptAsync(profile, result, runId)) historyWriteFailed = true;
                bool transient = result.Status is CollectionStatus.FailedConnection or CollectionStatus.TimedOut;
                if (!transient || retries >= _maximumRetries || cancellationToken.IsCancellationRequested) break;
                retries++;
                report(new BatchProgressUpdate(profile, "Retrying", $"Transient failure; retry {retries}/{_maximumRetries} is starting.", started));
                try { await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken); }
                catch (OperationCanceledException)
                {
                    result = CollectionAttemptResult.Failure(CollectionStatus.Cancelled, "Cancelled before the configured retry started.");
                    if (!await RecordAttemptAsync(profile, result, runId)) historyWriteFailed = true;
                    break;
                }
            }

            string state = result.Status switch
            {
                CollectionStatus.Succeeded => "Succeeded",
                CollectionStatus.TimedOut => "Timed out",
                CollectionStatus.Cancelled => "Cancelled",
                _ => "Failed"
            };
            string finalDetails = (result.IsSuccess ? result.ReportPath ?? result.Message : result.Message) + (historyWriteFailed ? " Profile attempt history could not be persisted." : string.Empty);
            AddOutcome(profile, state, result, finalDetails, started, DateTime.UtcNow);
        }

        return new BatchRunResult(runId, targets.Count, enabled, eligible, outcomes);

        void AddOutcome(ConnectionProfile profile, string state, CollectionAttemptResult result, string details, DateTime? started = null, DateTime? ended = null)
        {
            outcomes.Add(new BatchTargetOutcome(profile.Id, state, result));
            report(new BatchProgressUpdate(profile, state, details, started, ended));
        }
    }

    private async Task<bool> RecordAttemptAsync(ConnectionProfile profile, CollectionAttemptResult result, string runId)
    {
        try
        {
            await _profiles.RecordAttemptAsync(profile.Id, new ConnectionAttempt
            {
                RunId = string.IsNullOrWhiteSpace(result.RunId) ? Guid.NewGuid().ToString("N") : result.RunId,
                BatchRunId = runId,
                StartedUtc = result.StartedUtc ?? DateTime.UtcNow,
                EndedUtc = result.EndedUtc ?? DateTime.UtcNow,
                Status = result.Status.ToString(),
                Diagnostic = result.IsSuccess ? result.Diagnostic : Sanitize(result.Diagnostic ?? result.Message),
                ReportPath = result.IsSuccess ? result.ReportPath : null,
                ExecutablePath = result.ExecutablePath,
                RvToolsVersion = result.RvToolsVersion,
                ExitCode = result.ExitCode,
                SslOverrideUsed = false
            });
            return true;
        }
        catch { return false; /* The collection continues; the workspace receives its in-memory result. */ }
    }

    private static string HistoryWarning(bool recorded) => recorded ? string.Empty : " Profile attempt history could not be persisted.";
    private static string Sanitize(string value) => value.Length > 1600 ? value[^1600..] : value;
}
