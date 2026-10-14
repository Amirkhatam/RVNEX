using RVNEX.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace RVNEX.Services;

/// <summary>Read-only, dependency-free XLSX reader for RVTools workbooks.</summary>
internal sealed class WorkbookReader
{
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public Task<WorkbookData> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Read(path, cancellationToken), cancellationToken);

    public WorkbookData Read(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The workbook file was not found.", fullPath);
        }

        string hash;
        using (FileStream hashStream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hash = Convert.ToHexString(SHA256.HashData(hashStream));
        }

        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("This file does not contain an XLSX workbook.");

        XDocument workbook = LoadXml(workbookEntry, cancellationToken);
        WorkbookStyles styles = ReadStyles(archive.GetEntry("xl/styles.xml"), cancellationToken);
        bool date1904 = workbook.Root?.Element(SpreadsheetNs + "workbookPr")?.Attribute("date1904") is { } dateAttribute
            && (dateAttribute.Value == "1" || dateAttribute.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        XDocument relationships = archive.GetEntry("xl/_rels/workbook.xml.rels") is { } relEntry
            ? LoadXml(relEntry, cancellationToken)
            : throw new InvalidDataException("The workbook is missing its worksheet relationships.");

        var targets = relationships.Root?.Elements(PackageRelNs + "Relationship")
            .Where(element => element.Attribute("Id") is not null && element.Attribute("Target") is not null)
            .ToDictionary(element => (string)element.Attribute("Id")!, element => (string)element.Attribute("Target")!, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        List<string> sharedStrings = archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry
            ? ReadSharedStrings(sharedEntry, cancellationToken)
            : [];

        var warnings = new List<string>();
        var worksheets = new List<WorksheetData>();
        XElement[] sheetElements = workbook.Root?.Element(SpreadsheetNs + "sheets")?.Elements(SpreadsheetNs + "sheet").ToArray() ?? [];
        foreach (XElement sheetElement in sheetElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = (string?)sheetElement.Attribute("name") ?? "Unknown worksheet";
            string? relationshipId = (string?)sheetElement.Attribute(OfficeRelNs + "id");
            if (relationshipId is null || !targets.TryGetValue(relationshipId, out string? target))
            {
                warnings.Add($"Worksheet '{name}' has no readable relationship and was skipped.");
                continue;
            }

            string entryName = ResolveZipPath("xl/workbook.xml", target);
            ZipArchiveEntry? worksheetEntry = archive.GetEntry(entryName);
            if (worksheetEntry is null)
            {
                warnings.Add($"Worksheet '{name}' references a missing workbook part and was skipped.");
                continue;
            }

            worksheets.Add(ReadWorksheet(name, worksheetEntry, sharedStrings, styles, date1904, cancellationToken));
        }

        if (worksheets.Count == 0)
        {
            throw new InvalidDataException("The workbook does not contain a readable worksheet.");
        }

        return new WorkbookData
        {
            SourcePath = fullPath,
            Sha256 = hash,
            Worksheets = worksheets,
            Warnings = warnings
        };
    }

    private static WorksheetData ReadWorksheet(
        string name,
        ZipArchiveEntry entry,
        IReadOnlyList<string> sharedStrings,
        WorkbookStyles styles,
        bool date1904,
        CancellationToken cancellationToken)
    {
        var parsedRows = new List<(int RowNumber, CellValue[] Values)>();
        int widestColumn = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using (Stream stream = entry.Open())
        using (XmlReader sheetReader = XmlReader.Create(stream, settings))
        {
            while (sheetReader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheetReader.NodeType != XmlNodeType.Element
                    || sheetReader.LocalName != "row"
                    || sheetReader.NamespaceURI != SpreadsheetNs.NamespaceName)
                {
                    continue;
                }

                int rowNumber = int.TryParse(sheetReader.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedNumber)
                    ? parsedNumber
                    : parsedRows.Count + 1;
                var values = new SortedDictionary<int, CellValue>();
                int sequentialColumn = 0;
                using (XmlReader rowReader = sheetReader.ReadSubtree())
                {
                    rowReader.Read();
                    while (rowReader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (rowReader.NodeType != XmlNodeType.Element
                            || rowReader.LocalName != "c"
                            || rowReader.NamespaceURI != SpreadsheetNs.NamespaceName)
                        {
                            continue;
                        }
                        (string? reference, CellValue value) = ReadCell(rowReader, sharedStrings, styles, date1904);
                        int column = reference is null ? sequentialColumn : GetColumnIndex(reference);
                        sequentialColumn = column + 1;
                        values[column] = value;
                        widestColumn = Math.Max(widestColumn, column + 1);
                    }
                }

                if (values.Count > 0)
                {
                    var dense = Enumerable.Repeat(new CellValue(string.Empty, string.Empty), Math.Max(widestColumn, values.Keys.Max() + 1)).ToArray();
                    foreach ((int index, CellValue value) in values)
                    {
                        dense[index] = value;
                    }
                    parsedRows.Add((rowNumber, dense));
                }
            }
        }

        if (parsedRows.Count == 0)
        {
            return new WorksheetData { Name = name, Headers = [], RawHeaders = [], Rows = [] };
        }

        int headerRowIndex = 0;
        while (headerRowIndex < parsedRows.Count && parsedRows[headerRowIndex].Values.All(value => string.IsNullOrWhiteSpace(value.Raw)))
        {
            headerRowIndex++;
        }
        if (headerRowIndex >= parsedRows.Count)
        {
            return new WorksheetData { Name = name, Headers = [], RawHeaders = [], Rows = [] };
        }

        int columnCount = parsedRows.Max(row => row.Values.Length);
        var rawHeaders = Enumerable.Repeat(string.Empty, columnCount).ToArray();
        CellValue[] headerCells = parsedRows[headerRowIndex].Values;
        for (int i = 0; i < Math.Min(headerCells.Length, rawHeaders.Length); i++) rawHeaders[i] = headerCells[i].Raw;
        var headers = MakeUniqueHeaders(rawHeaders);
        var rows = new List<WorksheetRow>();
        for (int rowIndex = headerRowIndex + 1; rowIndex < parsedRows.Count; rowIndex++)
        {
            (int rowNumber, CellValue[] sourceValues) = parsedRows[rowIndex];
            var values = Enumerable.Repeat(string.Empty, headers.Count).ToArray();
            var displayValues = Enumerable.Repeat(string.Empty, headers.Count).ToArray();
            for (int i = 0; i < Math.Min(sourceValues.Length, values.Length); i++)
            {
                values[i] = sourceValues[i].Raw;
                displayValues[i] = sourceValues[i].Display;
            }
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            rows.Add(new WorksheetRow { SourceRowNumber = rowNumber, Values = values, DisplayValues = displayValues });
        }

        return new WorksheetData { Name = name, Headers = headers, RawHeaders = rawHeaders, Rows = rows };
    }

    private static (string? Reference, CellValue Value) ReadCell(
        XmlReader rowReader,
        IReadOnlyList<string> sharedStrings,
        WorkbookStyles styles,
        bool date1904)
    {
        using XmlReader cellReader = rowReader.ReadSubtree();
        cellReader.Read();
        string type = cellReader.GetAttribute("t") ?? string.Empty;
        string? reference = cellReader.GetAttribute("r");
        string? styleAttribute = cellReader.GetAttribute("s");
        string value = string.Empty;
        var inlineText = new System.Text.StringBuilder();
        while (cellReader.Read())
        {
            if (cellReader.NodeType != XmlNodeType.Element || cellReader.NamespaceURI != SpreadsheetNs.NamespaceName)
            {
                continue;
            }
            if (cellReader.LocalName == "v")
            {
                value = cellReader.ReadElementContentAsString();
            }
            else if (cellReader.LocalName == "t" && type == "inlineStr")
            {
                inlineText.Append(cellReader.ReadElementContentAsString());
            }
        }

        if (type == "inlineStr")
        {
            string text = inlineText.ToString();
            return (reference, new CellValue(text, text));
        }
        if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            string sharedText = index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : string.Empty;
            return (reference, new CellValue(sharedText, sharedText));
        }
        if (type == "b")
        {
            string boolean = value == "1" ? "TRUE" : value == "0" ? "FALSE" : value;
            return (reference, new CellValue(value, boolean));
        }
        string display = value;
        if (type is "" or "n" && int.TryParse(styleAttribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out int styleIndex)
            && styles.DateStyleIndexes.Contains(styleIndex)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial))
        {
            try
            {
                DateTime date = DateTime.FromOADate(serial + (date1904 ? 1462d : 0d));
                display = date.ToString(date.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                // Keep the exact cached value when the serial is outside the supported date range.
            }
        }
        return (reference, new CellValue(value, display));
    }

    private static WorkbookStyles ReadStyles(ZipArchiveEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null) return new WorkbookStyles(new HashSet<int>());
        XDocument document = LoadXml(entry, cancellationToken);
        var formats = document.Root?.Element(SpreadsheetNs + "numFmts")?.Elements(SpreadsheetNs + "numFmt")
            .Where(element => int.TryParse((string?)element.Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .ToDictionary(
                element => int.Parse((string)element.Attribute("numFmtId")!, CultureInfo.InvariantCulture),
                element => (string?)element.Attribute("formatCode") ?? string.Empty)
            ?? new Dictionary<int, string>();
        var dateStyles = new HashSet<int>();
        XElement[] xfs = document.Root?.Element(SpreadsheetNs + "cellXfs")?.Elements(SpreadsheetNs + "xf").ToArray() ?? [];
        for (int styleIndex = 0; styleIndex < xfs.Length; styleIndex++)
        {
            if (!int.TryParse((string?)xfs[styleIndex].Attribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatId)) continue;
            bool builtinDate = formatId is >= 14 and <= 22 or >= 45 and <= 47;
            bool customDate = formats.TryGetValue(formatId, out string? formatCode) && LooksLikeDateFormat(formatCode);
            if (builtinDate || customDate) dateStyles.Add(styleIndex);
        }
        return new WorkbookStyles(dateStyles);
    }

    private static bool LooksLikeDateFormat(string formatCode)
    {
        string normalized = Regex.Replace(formatCode, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", string.Empty);
        return Regex.IsMatch(normalized, "[ymdhis]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        XDocument document = LoadXml(entry, cancellationToken);
        return document.Root?.Elements(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToList() ?? [];
    }

    private static List<string> MakeUniqueHeaders(IReadOnlyList<string> rawHeaders)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>(rawHeaders.Count);
        for (int i = 0; i < rawHeaders.Count; i++)
        {
            string baseName = string.IsNullOrWhiteSpace(rawHeaders[i]) ? $"Column {i + 1}" : rawHeaders[i].Trim();
            string candidate = baseName;
            int duplicate = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{baseName} ({duplicate++})";
            }
            headers.Add(candidate);
        }
        return headers;
    }

    private static int GetColumnIndex(string cellReference)
    {
        int index = 0;
        foreach (char character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }
            index = checked(index * 26 + (char.ToUpperInvariant(character) - 'A' + 1));
        }
        return Math.Max(0, index - 1);
    }

    private static string ResolveZipPath(string basePath, string target)
    {
        string combined = target.StartsWith('/') ? target[1..] :
            $"{Path.GetDirectoryName(basePath)?.Replace('\\', '/')}/{target}";
        var parts = new List<string>();
        foreach (string part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private sealed record CellValue(string Raw, string Display);
    private sealed record WorkbookStyles(HashSet<int> DateStyleIndexes);
}
