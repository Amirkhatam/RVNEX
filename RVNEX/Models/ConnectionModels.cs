using System;
using System.Collections.Generic;

namespace RVNEX.Models;

public enum EndpointType
{
    VCenterServer,
    StandaloneEsxi
}

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public EndpointType EndpointType { get; set; } = EndpointType.VCenterServer;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    public string Username { get; set; } = string.Empty;
    public string CredentialTarget { get; set; } = string.Empty;
    public bool CredentialSaved { get; set; }
    public string? Group { get; set; }
    public string? Notes { get; set; }
    public bool Enabled { get; set; } = true;
    public string? RvToolsExecutablePath { get; set; }
    public bool? UseSslCertificateOverride { get; set; }
    public int SortOrder { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LatestResult { get; set; }
    public string? LatestReportPath { get; set; }
    public string? LastErrorSummary { get; set; }
    public List<ConnectionAttempt> Attempts { get; set; } = [];
}

public sealed class ConnectionAttempt
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string? BatchRunId { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Diagnostic { get; set; }
    public string? ReportPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string? RvToolsVersion { get; set; }
    public int? ExitCode { get; set; }
    public bool SslOverrideUsed { get; set; }
}

public sealed class ConnectionCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public List<BatchRunRecord> BatchRuns { get; set; } = [];
    public bool ConfirmBulkCollection { get; set; }
}

public sealed class BatchRunRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public int Configured { get; set; }
    public int Enabled { get; set; }
    public int Eligible { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Cancelled { get; set; }
    public string Summary { get; set; } = string.Empty;
}
