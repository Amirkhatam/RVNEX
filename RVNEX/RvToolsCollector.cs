using Microsoft.Win32;
using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RVNEX;

/// <summary>Runs the installed RVTools exporter and accepts an output only after process and XLSX validation.</summary>
internal sealed class RvToolsCollector
{
    private static readonly TimeSpan DefaultCollectionTimeout = TimeSpan.FromMinutes(30);
    private const string ExportCommand = "ExportAll2xlsx";

    public async Task<CollectionAttemptResult> CollectAsync(
        ConnectionProfile profile,
        string password,
        IProgress<CollectionProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null,
        bool retainReport = true)
    {
        string runId = Guid.NewGuid().ToString("N");
        DateTime startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string version = "Unknown version";
        string outputDirectory = string.Empty;
        string reportPath = string.Empty;
        string? executablePath = string.IsNullOrWhiteSpace(profile.RvToolsExecutablePath)
            ? RvToolsInstallationLocator.FindExecutable()
            : Path.GetFullPath(profile.RvToolsExecutablePath);
        if (executablePath is null || !File.Exists(executablePath))
            return Fail(CollectionStatus.MissingRvTools, "The RVTools executable selected for this profile is missing. Select an installed RVTools executable in Connection Manager.");

        FileVersionInfo fileVersion = FileVersionInfo.GetVersionInfo(executablePath);
        version = string.IsNullOrWhiteSpace(fileVersion.ProductVersion) ? fileVersion.FileVersion ?? "Unknown version" : fileVersion.ProductVersion;
        var versions = RvToolsInstallationLocator.FindExecutables();
        bool supported = versions.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase));
        if (!supported)
            return Fail(CollectionStatus.MissingRvTools, "The selected RVTools executable was not found through an installed RVTools registration or standard installation location.");

        Match versionMatch = Regex.Match(version, @"\d+(?:\.\d+){1,3}");
        if (!Version.TryParse(versionMatch.Value, out Version? parsedVersion) || parsedVersion < new Version(4, 5, 0))
            return Fail(CollectionStatus.FailedExecution, "This RVNEX build requires RVTools 4.5 or later so it can use RVTools' DPAPI-protected command-line password format. No plaintext password was passed.");

        string protectedPassword;
        try { protectedPassword = RvToolsPasswordEncryptor.EncryptForRvTools(password); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return Fail(CollectionStatus.FailedExecution, $"RVNEX could not protect the password for RVTools {version}. The export was not started. ({ex.GetType().Name})");
        }

        progress?.Report(new CollectionProgress($"Preparing RVTools {version} export for {profile.Name}..."));
        try
        {
            outputDirectory = retainReport
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RVNEX Reports")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RVNEX", "ConnectionTests", runId);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new IOException("The user output folder is unavailable.");
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail(CollectionStatus.FailedExecution, "RVNEX could not prepare a writable output folder. Check the user Documents folder permissions.");
        }

        string fileName = CreateReportFileName(profile, runId);
        reportPath = Path.Combine(outputDirectory, fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // RVTools 4.5+ documents ExportAll2xlsx and DPAPI-protected passwords. ArgumentList
        // avoids shell parsing; the -p argument contains no plaintext password.
        startInfo.ArgumentList.Add("-s"); startInfo.ArgumentList.Add(profile.Address);
        startInfo.ArgumentList.Add("-u"); startInfo.ArgumentList.Add(profile.Username);
        startInfo.ArgumentList.Add("-p"); startInfo.ArgumentList.Add(protectedPassword);
        startInfo.ArgumentList.Add("-c"); startInfo.ArgumentList.Add(ExportCommand);
        startInfo.ArgumentList.Add("-d"); startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-f"); startInfo.ArgumentList.Add(fileName);
        bool bypassSslCheck = profile.UseSslCertificateOverride == true;
        if (bypassSslCheck) startInfo.ArgumentList.Add("-BypassSSLCheck");

        progress?.Report(new CollectionProgress(bypassSslCheck
            ? $"Connecting to {profile.Address}; RVTools is collecting inventory with SSL certificate validation bypassed for this profile."
            : $"Connecting to {profile.Address}; RVTools is collecting inventory. Progress inside RVTools is not available."));
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        bool processStarted = false;
        try
        {
            if (!process.Start())
                return Fail(CollectionStatus.FailedExecution, "RVTools could not be started.", diagnostic: BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, false, null, stopwatch.Elapsed, reportPath, "Process.Start returned false.", password, protectedPassword, profile.Username));
            processStarted = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Fail(CollectionStatus.FailedExecution, "RVTools could not be started. Check its installation and application permissions.", diagnostic: BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, false, null, stopwatch.Elapsed, reportPath, $"Start error type: {ex.GetType().Name}.", password, protectedPassword, profile.Username));
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        TimeSpan timeoutSpan = timeoutOverride ?? DefaultCollectionTimeout;
        bool reportedUnrecognizedWindow = false;
        while (!process.HasExited)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                bool stopped = TryStopOwnedProcess(process);
                string output = Sanitize(await ReadOutputAsync(stdout, stderr), password, protectedPassword, profile.Username);
                int? cancelledExitCode = process.HasExited ? process.ExitCode : null;
                string diagnostic = BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, processStarted, cancelledExitCode, stopwatch.Elapsed, reportPath, output, password, protectedPassword, profile.Username);
                TryDeleteReport(reportPath);
                string message = stopped ? "Collection was cancelled. The launched RVTools process stopped; no report was accepted." : "Collection was cancelled, but RVNEX could not confirm that the launched RVTools process stopped. Inspect that process before retrying; no report was accepted.";
                return Fail(CollectionStatus.Cancelled, message, cancelledExitCode, diagnostic);
            }
            if (stopwatch.Elapsed >= timeoutSpan)
            {
                bool stopped = TryStopOwnedProcess(process);
                string output = Sanitize(await ReadOutputAsync(stdout, stderr), password, protectedPassword, profile.Username);
                int? timeoutExitCode = process.HasExited ? process.ExitCode : null;
                string diagnostic = BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, processStarted, timeoutExitCode, stopwatch.Elapsed, reportPath, output, password, protectedPassword, profile.Username);
                TryDeleteReport(reportPath);
                string cleanup = stopped ? "The launched process was stopped." : "RVNEX could not confirm that the launched process stopped; inspect it before retrying.";
                return Fail(CollectionStatus.TimedOut, $"RVTools did not finish within {timeoutSpan.TotalMinutes:0} minutes (elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}). {cleanup} No report was accepted.", timeoutExitCode, diagnostic);
            }

            NativeRvToolsDialog? dialog = NativeRvToolsDialogMonitor.FindRecognizedFailure(process.Id);
            if (dialog is not null)
            {
                progress?.Report(new CollectionProgress("RVTools displayed an error dialog. Recording it and closing only that RVTools-owned dialog..."));
                bool closePosted = NativeRvToolsDialogMonitor.TryClose(dialog, process.Id);
                bool exitedAfterClose = false;
                if (closePosted)
                {
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); exitedAfterClose = true; }
                    catch (TimeoutException) { TryStopOwnedProcess(process); }
                }
                else
                {
                    TryStopOwnedProcess(process);
                }
                string output = Sanitize(await ReadOutputAsync(stdout, stderr), password, protectedPassword, profile.Username);
                int? dialogExitCode = process.HasExited ? process.ExitCode : null;
                string diagnostic = BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, processStarted, dialogExitCode, stopwatch.Elapsed, reportPath, Sanitize(dialog.Text, password, protectedPassword, profile.Username) + (output.Length == 0 ? string.Empty : " " + output), password, protectedPassword, profile.Username);
                TryDeleteReport(reportPath);
                CollectionStatus status = ClassifyNativeDialog(dialog.Title, dialog.Text);
                string summary = status switch
                {
                    CollectionStatus.FailedAuthentication => "RVTools reported an authentication failure.",
                    CollectionStatus.FailedTlsOrCertificate => "RVTools explicitly reported a certificate or SSL validation failure.",
                    CollectionStatus.UnresolvedTlsFailure => "RVTools reported an SSL/TLS secure-channel failure; the exact protocol-versus-certificate cause is unresolved.",
                    _ => "RVTools reported a connection failure."
                };
                string closeState = exitedAfterClose
                    ? "The positively identified dialog closed and the launched RVTools process exited."
                    : process.HasExited
                        ? "The close request did not complete, so RVNEX stopped only the launched RVTools process tree."
                        : "RVNEX could not confirm that the launched RVTools process stopped; inspect it before retrying.";
                return Fail(status, $"{summary} {closeState} Elapsed {stopwatch.Elapsed:hh\\:mm\\:ss}. No report was accepted.", dialogExitCode, diagnostic);
            }

            if (!reportedUnrecognizedWindow && NativeRvToolsDialogMonitor.HasUnrecognizedOwnedWindow(process.Id))
            {
                progress?.Report(new CollectionProgress("RVTools is waiting in an application window. RVNEX is monitoring the process; inspect the RVTools window if it needs user input."));
                reportedUnrecognizedWindow = true;
            }
            await Task.Delay(300).ConfigureAwait(false);
        }

        string processOutput = Sanitize(await ReadOutputAsync(stdout, stderr), password, protectedPassword, profile.Username);
        int exitCode = process.ExitCode;
        string processDiagnostic = BuildProcessDiagnostic(executablePath, version, startInfo.WorkingDirectory, profile.Address, outputDirectory, fileName, bypassSslCheck, processStarted, exitCode, stopwatch.Elapsed, reportPath, processOutput, password, protectedPassword, profile.Username);
        if (TryClassifyText(processOutput, out CollectionStatus outputStatus))
        {
            TryDeleteReport(reportPath);
            return Fail(outputStatus, StatusMessage(outputStatus, "RVTools returned an error."), exitCode, processDiagnostic);
        }
        if (exitCode != 0)
        {
            TryDeleteReport(reportPath);
            CollectionStatus status = exitCode == -1 ? CollectionStatus.FailedConnection : CollectionStatus.FailedExecution;
            string message;
            if (exitCode == -1)
            {
                string tlsGuidance = bypassSslCheck
                    ? "SSL certificate bypass was enabled for this profile; check the endpoint, credentials, network, and RVTools connection details."
                    : "SSL certificate validation remains enabled. RVTools 4.8.1+ can fail on an untrusted server certificate; trust the server certificate chain or explicitly allow bypass for this profile after verifying the server identity.";
                string loggingGuidance = processOutput.Contains("Failed to find configuration section 'log4net'", StringComparison.OrdinalIgnoreCase)
                    ? " The accompanying log4net message concerns logging configuration and does not identify the connection failure."
                    : string.Empty;
                message = $"RVTools exited with code -1 (its installed batch sample describes this as probably a connection failure). {tlsGuidance}{loggingGuidance} No report was accepted.";
            }
            else
            {
                message = $"RVTools exited with code {exitCode}. No report was accepted.";
            }
            return Fail(status, message, exitCode, processDiagnostic);
        }

        progress?.Report(new CollectionProgress("RVTools exited successfully. Verifying the generated workbook..."));
        if (!IsValidXlsx(reportPath))
        {
            processDiagnostic += " Workbook validation: failed.";
            TryDeleteReport(reportPath);
            return Fail(CollectionStatus.FailedWorkbookValidation, "RVTools exited with code 0, but its output was missing or was not a valid XLSX workbook.", exitCode, processDiagnostic);
        }

        processDiagnostic += " Workbook validation: passed.";
        if (!retainReport)
        {
            TryDeleteReport(reportPath);
            TryDeleteTemporaryDirectory(outputDirectory);
        }
        return new CollectionAttemptResult(
            CollectionStatus.Succeeded,
            retainReport
                ? $"RVTools {version} exited with code 0 in {stopwatch.Elapsed:hh\\:mm\\:ss}. Verified report: {reportPath}"
                : $"RVTools {version} completed a real export and produced a valid XLSX in {stopwatch.Elapsed:hh\\:mm\\:ss}. The temporary test report was deleted.",
            retainReport ? reportPath : null,
            version,
            runId,
            startedUtc,
            DateTime.UtcNow,
            exitCode,
            executablePath,
            processDiagnostic);

        CollectionAttemptResult Fail(CollectionStatus status, string message, int? code = null, string? diagnostic = null)
        {
            if (!retainReport)
            {
                TryDeleteReport(reportPath);
                TryDeleteTemporaryDirectory(outputDirectory);
            }
            return new CollectionAttemptResult(status, message, null, version, runId, startedUtc, DateTime.UtcNow, code, executablePath, TrimDiagnostic(diagnostic));
        }
    }

    private static string CreateReportFileName(ConnectionProfile profile, string runId)
    {
        string endpoint = SafeFilePart(profile.Address);
        string environment = SafeFilePart(profile.Group ?? profile.Name);
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        return $"{environment}-{endpoint}-{timestamp}-{runId[..8]}.xlsx";
    }

    private static string BuildProcessDiagnostic(
        string executablePath,
        string version,
        string workingDirectory,
        string address,
        string outputDirectory,
        string fileName,
        bool bypassSslCheck,
        bool processStarted,
        int? exitCode,
        TimeSpan elapsed,
        string reportPath,
        string? output,
        params string[] sensitiveValues)
    {
        bool outputExists = File.Exists(reportPath);
        long? outputBytes = null;
        if (outputExists)
        {
            try { outputBytes = new FileInfo(reportPath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string arguments = $"-s {address} -u [redacted] -p [redacted] -c {ExportCommand} -d \"{outputDirectory}\" -f \"{fileName}\"" + (bypassSslCheck ? " -BypassSSLCheck" : string.Empty);
        string safeOutput = Sanitize(output, sensitiveValues);
        return TrimDiagnostic($"Executable: {executablePath}; Version: {version}; Working directory: {workingDirectory}; Arguments: {arguments}; Process started: {processStarted}; Exit code: {(exitCode.HasValue ? exitCode.Value.ToString() : "not available")}; Elapsed: {elapsed.TotalMilliseconds:0} ms; Output file exists: {outputExists}; Output size: {(outputBytes.HasValue ? outputBytes.Value.ToString() + " bytes" : "not available")}; RVTools stdout/stderr (sanitized): {safeOutput}");
    }

    private static string SafeFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(c => invalid.Contains(c) || c is '/' or '\\' or ':' ? '-' : c).ToArray());
        result = Regex.Replace(result, @"[^A-Za-z0-9._-]+", "-").Trim('.', '-', '_');
        return result.Length > 48 ? result[..48] : string.IsNullOrWhiteSpace(result) ? "endpoint" : result;
    }

    private static bool IsValidXlsx(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 100) return false;
            using var archive = ZipFile.OpenRead(path);
            ZipArchiveEntry? contentTypes = archive.GetEntry("[Content_Types].xml");
            ZipArchiveEntry? relationships = archive.GetEntry("_rels/.rels");
            ZipArchiveEntry? workbook = archive.GetEntry("xl/workbook.xml");
            if (contentTypes is null || relationships is null || workbook is null) return false;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(workbook.Open(), settings);
            XDocument document = XDocument.Load(reader);
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            return document.Root?.Element(ns + "sheets")?.Elements(ns + "sheet").Any() == true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool TryClassifyText(string text, out CollectionStatus status)
    {
        status = default;
        string lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "invalidlogin", "invalid login", "authentication failed", "failed to authenticate", "incorrect username or password", "incorrect user name or password", "http status 401", "invalid credentials"))
        { status = CollectionStatus.FailedAuthentication; return true; }
        if (ContainsAny(lower, "certificate validation failed", "certificate is invalid", "certificate chain", "untrusted certificate"))
        { status = CollectionStatus.FailedTlsOrCertificate; return true; }
        if (ContainsAny(lower, "could not create ssl/tls secure channel", "ssl/tls", "ssl error", "tls handshake", "secure channel", "ssl exception"))
        { status = CollectionStatus.UnresolvedTlsFailure; return true; }
        if (ContainsAny(lower, "unable to connect", "could not connect", "connection refused", "connection timed out", "no such host", "host not found", "name or service not known"))
        { status = CollectionStatus.FailedConnection; return true; }
        return false;
    }

    private static CollectionStatus ClassifyDialog(string text)
    {
        TryClassifyText(text, out CollectionStatus status);
        return status;
    }

    internal static CollectionStatus ClassifyNativeDialog(string title, string text) => ClassifyDialog(title + " " + text);

    private static string StatusMessage(CollectionStatus status, string fallback) => status switch
    {
        CollectionStatus.FailedAuthentication => "RVTools reported an authentication failure. Check the account and its vSphere access.",
        CollectionStatus.FailedConnection => "RVTools reported a connection failure. Check the address, network path, and server availability.",
        CollectionStatus.FailedTlsOrCertificate => "RVTools reported a certificate validation failure. Repair trust for the server certificate through your normal security process.",
        CollectionStatus.UnresolvedTlsFailure => "RVTools reported a TLS secure-channel error. Protocol negotiation and certificate validation remain unresolved.",
        _ => fallback
    };

    private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);

    private static async Task<string> ReadOutputAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return $"stdout: {stdout.Result}\nstderr: {stderr.Result}";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException) { return string.Empty; }
    }

    private static string Sanitize(string? value, params string[] sensitiveValues)
    {
        string safe = value ?? string.Empty;
        foreach (string sensitiveValue in sensitiveValues)
            if (!string.IsNullOrEmpty(sensitiveValue)) safe = safe.Replace(sensitiveValue, "[redacted]", StringComparison.Ordinal);
        safe = Regex.Replace(safe, @"(?i)(-p\s+)(?:""[^""]*""|\S+)", "$1[redacted]");
        safe = Regex.Replace(safe, @"(?i)(password\s*[:=]\s*)\S+", "$1[redacted]");
        safe = Regex.Replace(safe, @"\s+", " ").Trim();
        return safe.Length > 1600 ? safe[^1600..] : safe;
    }

    private static string TrimDiagnostic(string? value) => string.IsNullOrWhiteSpace(value) ? "No diagnostic text was provided by RVTools." : value.Length > 1600 ? value[^1600..] : value;

    private static bool TryStopOwnedProcess(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return process.HasExited;
        }
        catch (InvalidOperationException) { return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteReport(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteTemporaryDirectory(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

internal static class NativeRvToolsDialogMonitor
{
    private const uint WmClose = 0x0010;
    private static readonly Regex PositiveFailure = new("(?i)(could not connect|unable to connect|invalid login|authentication failed|ssl.?tls|secure channel|certificate|connection refused|connection timed out|unknown error)", RegexOptions.Compiled);

    public static NativeRvToolsDialog? FindRecognizedFailure(int processId)
    {
        NativeRvToolsDialog? match = null;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != (uint)processId) return true;
            string title = GetText(window);
            string body = GetStaticChildrenText(window, processId);
            string combined = (title + " " + body).Trim();
            if (title.Contains("RVTools", StringComparison.OrdinalIgnoreCase) && PositiveFailure.IsMatch(combined))
            {
                match = new NativeRvToolsDialog(window, title, body);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    public static bool HasUnrecognizedOwnedWindow(int processId)
    {
        bool found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == (uint)processId && IsWindowVisible(window) && GetText(window).Contains("RVTools", StringComparison.OrdinalIgnoreCase))
            { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool TryClose(NativeRvToolsDialog dialog, int processId)
    {
        if (!IsWindow(dialog.Handle)) return true;
        GetWindowThreadProcessId(dialog.Handle, out uint owner);
        if (owner != (uint)processId || !dialog.Title.Contains("RVTools", StringComparison.OrdinalIgnoreCase) || !PositiveFailure.IsMatch(dialog.Text)) return false;
        return PostMessage(dialog.Handle, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static string GetStaticChildrenText(IntPtr parent, int processId)
    {
        var parts = new List<string>();
        EnumChildWindows(parent, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint owner);
            if (owner != (uint)processId) return true;
            var className = new StringBuilder(128);
            _ = GetClassName(child, className, className.Capacity);
            if (className.ToString().Contains("STATIC", StringComparison.OrdinalIgnoreCase) || className.ToString().Contains("Label", StringComparison.OrdinalIgnoreCase))
            {
                string text = GetText(child);
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
            }
            return true;
        }, IntPtr.Zero);
        return string.Join(" ", parts);
    }

    private static string GetText(IntPtr window)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0 || length > 4096) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

internal sealed record NativeRvToolsDialog(IntPtr Handle, string Title, string Text);

internal static class RvToolsInstallationLocator
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static string? FindExecutable() => FindExecutables().FirstOrDefault();

    public static IReadOnlyList<string> FindExecutables()
    {
        var candidates = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            AddRegisteredInstallations(candidates, hive, view);
        AddRobwareFolderCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddRobwareFolderCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddDellFolderCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        AddDellFolderCandidates(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
            foreach (string path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.Add(Path.Combine(path, "RVTools.exe"));

        return candidates.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => FileVersionInfo.GetVersionInfo(path).FileVersion, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddRegisteredInstallations(List<string> candidates, RegistryHive hive, RegistryView view)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
            if (uninstallKey is null) return;
            foreach (string subKeyName in uninstallKey.GetSubKeyNames())
            {
                using RegistryKey? entry = uninstallKey.OpenSubKey(subKeyName);
                if (entry?.GetValue("DisplayName") is not string displayName || !displayName.Contains("RVTools", StringComparison.OrdinalIgnoreCase)) continue;
                AddInstallationPath(candidates, entry.GetValue("InstallLocation") as string);
                AddInstallationPath(candidates, entry.GetValue("DisplayIcon") as string);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
    }

    private static void AddRobwareFolderCandidates(List<string> candidates, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return;
        string robwarePath = Path.Combine(programFiles, "Robware");
        try
        {
            if (!Directory.Exists(robwarePath)) return;
            foreach (string directory in Directory.EnumerateDirectories(robwarePath, "RVTools*", SearchOption.TopDirectoryOnly))
                candidates.Add(Path.Combine(directory, "RVTools.exe"));
            candidates.Add(Path.Combine(robwarePath, "RVTools.exe"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException) { }
    }

    private static void AddDellFolderCandidates(List<string> candidates, string? programFiles)
    {
        if (string.IsNullOrWhiteSpace(programFiles)) return;
        string dellPath = Path.Combine(programFiles, "Dell");
        string directPath = Path.Combine(dellPath, "RVTools");
        candidates.Add(Path.Combine(directPath, "RVTools.exe"));
        try
        {
            if (!Directory.Exists(dellPath)) return;
            foreach (string directory in Directory.EnumerateDirectories(dellPath, "RVTools*", SearchOption.TopDirectoryOnly))
                candidates.Add(Path.Combine(directory, "RVTools.exe"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException) { }
    }

    private static void AddInstallationPath(List<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string path = value.Trim().Trim('"');
        int iconIndex = path.IndexOf(',');
        if (iconIndex >= 0) path = path[..iconIndex].Trim().Trim('"');
        if (Directory.Exists(path)) candidates.Add(Path.Combine(path, "RVTools.exe"));
        else if (string.Equals(Path.GetFileName(path), "RVTools.exe", StringComparison.OrdinalIgnoreCase)) candidates.Add(path);
    }
}
