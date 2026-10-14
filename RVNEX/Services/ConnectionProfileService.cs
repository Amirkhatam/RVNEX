using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RVNEX.Services;

internal sealed class ConnectionProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _catalogPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConnectionCatalog? _catalog;

    public ConnectionProfileService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RVNEX", "connections.json")) { }

    internal ConnectionProfileService(string catalogPath) => _catalogPath = Path.GetFullPath(catalogPath);

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            return _catalog.Profiles.OrderBy(p => p.SortOrder).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BatchRunRecord>> LoadBatchRunsAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            return (_catalog.BatchRuns ??= []).OrderByDescending(r => r.EndedUtc).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task RecordBatchRunAsync(BatchRunRecord record, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            _catalog.BatchRuns ??= [];
            _catalog.BatchRuns.RemoveAll(run => run.RunId == record.RunId);
            _catalog.BatchRuns.Insert(0, record);
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ConnectionProfile profile, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            ConnectionProfile? existing = _catalog.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (_catalog.Profiles.Any(p => p.Id != profile.Id && string.Equals(p.Name.Trim(), profile.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A connection profile already uses that name.");

            profile.Name = profile.Name.Trim();
            profile.Address = profile.Address.Trim();
            profile.Username = profile.Username.Trim();
            profile.Group = string.IsNullOrWhiteSpace(profile.Group) ? null : profile.Group.Trim();
            profile.Notes = string.IsNullOrWhiteSpace(profile.Notes) ? null : profile.Notes.Trim();
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(profile.CredentialTarget)) profile.CredentialTarget = CredentialVault.GetTarget(profile.Id);
            if (existing is null)
            {
                profile.SortOrder = _catalog.Profiles.Count == 0 ? 0 : _catalog.Profiles.Max(p => p.SortOrder) + 1;
                _catalog.Profiles.Add(profile);
            }
            else
            {
                int index = _catalog.Profiles.IndexOf(existing);
                profile.SortOrder = existing.SortOrder;
                profile.Attempts = existing.Attempts;
                profile.LastAttemptUtc = existing.LastAttemptUtc;
                profile.LastSuccessUtc = existing.LastSuccessUtc;
                profile.LatestResult = existing.LatestResult;
                profile.LatestReportPath = existing.LatestReportPath;
                profile.LastErrorSummary = existing.LastErrorSummary;
                _catalog.Profiles[index] = profile;
            }
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task<ConnectionProfile> DuplicateAsync(string id, CancellationToken token = default)
    {
        ConnectionProfile source = (await LoadAsync(token)).FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException("The selected profile no longer exists.");
        string newId = Guid.NewGuid().ToString("N");
        var copy = new ConnectionProfile
        {
            Id = newId,
            Name = CreateUniqueCopyName(source.Name, (await LoadAsync(token)).Select(p => p.Name)),
            EndpointType = source.EndpointType,
            Address = source.Address,
            Port = source.Port,
            Username = source.Username,
            CredentialTarget = CredentialVault.GetTarget(newId),
            CredentialSaved = false,
            Group = source.Group,
            Notes = source.Notes,
            Enabled = source.Enabled,
            RvToolsExecutablePath = source.RvToolsExecutablePath,
            UseSslCertificateOverride = source.UseSslCertificateOverride
        };
        await SaveAsync(copy, token);
        return copy;
    }

    public async Task DeleteAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            ConnectionProfile? profile = _catalog.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return;
            CredentialVault.Delete(profile.CredentialTarget);
            _catalog.Profiles.Remove(profile);
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task MoveAsync(string id, int offset, CancellationToken token = default)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            List<ConnectionProfile> ordered = _catalog.Profiles.OrderBy(p => p.SortOrder).ToList();
            int current = ordered.FindIndex(p => p.Id == id);
            int target = current + offset;
            if (current < 0 || target < 0 || target >= ordered.Count) return;
            (ordered[current].SortOrder, ordered[target].SortOrder) = (ordered[target].SortOrder, ordered[current].SortOrder);
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public async Task RecordAttemptAsync(string id, ConnectionAttempt attempt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            ConnectionProfile? profile = _catalog.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile is null) return;
            profile.LastAttemptUtc = attempt.EndedUtc;
            profile.LatestResult = attempt.Status;
            profile.LatestReportPath = attempt.Status == "Succeeded" ? attempt.ReportPath : profile.LatestReportPath;
            profile.LastErrorSummary = attempt.Status == "Succeeded" ? null : attempt.Diagnostic;
            if (attempt.Status == "Succeeded") profile.LastSuccessUtc = attempt.EndedUtc;
            profile.Attempts.Add(attempt);
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public Task<bool> HasCredentialAsync(ConnectionProfile profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(profile.CredentialSaved);
    }

    public async Task MarkCredentialSavedAsync(string profileId, bool saved, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            _catalog ??= await ReadCatalogAsync(token);
            ConnectionProfile? profile = _catalog.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile is null || profile.CredentialSaved == saved) return;
            profile.CredentialSaved = saved;
            await SaveCatalogAsync(token);
        }
        finally { _gate.Release(); }
    }

    public Task SaveCredentialAsync(ConnectionProfile profile, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Enter a password before saving credentials.", nameof(password));
        CredentialVault.Write(profile.CredentialTarget, profile.Username, password);
        return Task.CompletedTask;
    }

    public Task<string?> GetCredentialAsync(ConnectionProfile profile)
    {
        string? value = CredentialVault.Read(profile.CredentialTarget);
        return Task.FromResult(value);
    }

    public Task DeleteCredentialAsync(ConnectionProfile profile)
    {
        CredentialVault.Delete(profile.CredentialTarget);
        return Task.CompletedTask;
    }

    private async Task<ConnectionCatalog> ReadCatalogAsync(CancellationToken token)
    {
        if (!File.Exists(_catalogPath)) return new ConnectionCatalog();
        try
        {
            await using var input = new FileStream(_catalogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ConnectionCatalog? catalog = await JsonSerializer.DeserializeAsync<ConnectionCatalog>(input, JsonOptions, token);
            return catalog is { SchemaVersion: 1, Profiles: not null } ? catalog : new ConnectionCatalog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The connection profile catalog could not be read. Its original file was left untouched.", ex);
        }
    }

    private async Task SaveCatalogAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        string temp = _catalogPath + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32768, true))
            {
                await JsonSerializer.SerializeAsync(output, _catalog, JsonOptions, token);
                await output.FlushAsync(token);
            }
            if (File.Exists(_catalogPath)) File.Replace(temp, _catalogPath, _catalogPath + ".bak", true);
            else File.Move(temp, _catalogPath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(ConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("Enter a profile name.");
        if (!IsValidAddress(profile.Address)) throw new InvalidOperationException("Enter a host name or IP address without a scheme or path.");
        if (profile.Port is < 1 or > 65535) throw new InvalidOperationException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(profile.Username)) throw new InvalidOperationException("Enter a username.");
    }

    internal static bool IsValidAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || address.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0) return false;
        if (IPAddress.TryParse(address, out _)) return true;
        return address.IndexOf(':') < 0 && Uri.CheckHostName(address) == UriHostNameType.Dns;
    }

    private static string CreateUniqueCopyName(string source, IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        string candidate = source + " copy";
        for (int suffix = 2; set.Contains(candidate); suffix++) candidate = $"{source} copy {suffix}";
        return candidate;
    }
}

internal static class CredentialVault
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string GetTarget(string profileId) => $"RVNEX/Profile/{profileId}";

    public static void Write(string target, string username, string password)
    {
        IntPtr blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = username,
                CredentialBlob = blob,
                CredentialBlobSize = checked((uint)(password.Length * sizeof(char))),
                Persist = PersistLocalMachine
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save this credential.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read this credential.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            if (credential.CredentialBlobSize > 5120) throw new InvalidDataException("The saved credential has an unsupported size.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally { CredFree(pointer); }
    }

    public static void Delete(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!CredDelete(target, GenericCredential, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error, "Windows Credential Manager could not remove this credential.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredFree")] private static extern void CredFree(IntPtr credential);
}
