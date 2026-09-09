using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SaaSApp.Reporting.Application.Contracts;

namespace SaaSApp.Reporting.Infrastructure.Services;

public sealed class ReportExportService : IReportExportService
{
    public ReportFileResult Export(ReportRunResult data, string? format)
    {
        var kind = (format ?? "PDF").Trim();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeName = SanitizeFileName(data.Name);

        if (kind.Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            var csv = BuildCsv(data);
            return new ReportFileResult(
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv",
                $"{safeName}-{stamp}.csv");
        }

        if (kind.Equals("Excel", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("XLS", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("XLSX", StringComparison.OrdinalIgnoreCase))
        {
            return new ReportFileResult(
                Encoding.UTF8.GetBytes(BuildSpreadsheetMl(data)),
                "application/vnd.ms-excel",
                $"{safeName}-{stamp}.xls");
        }

        try
        {
            return new ReportFileResult(BuildPdf(data), "application/pdf", $"{safeName}-{stamp}.pdf");
        }
        catch
        {
            var csv = BuildCsv(data);
            return new ReportFileResult(
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(),
                "text/csv",
                $"{safeName}-{stamp}.csv");
        }
    }

    private static byte[] BuildPdf(ReportRunResult data)
    {
        EnsureFonts();
        using var document = new PdfDocument();
        document.Info.Title = data.Name;

        var landscape = data.Columns.Count > 5;
        var page = AddPage(document, landscape);
        var gfx = XGraphics.FromPdfPage(page);
        var titleFont = new XFont("Arial", 14, XFontStyleEx.Bold);
        var headerFont = new XFont("Arial", 8, XFontStyleEx.Bold);
        var cellFont = new XFont("Arial", 7, XFontStyleEx.Regular);

        const double margin = 28;
        const double rowHeight = 16;
        var y = margin;
        gfx.DrawString(data.Name, titleFont, XBrushes.Black, new XPoint(margin, y));
        y += 18;
        gfx.DrawString(
            $"{data.Domain}  ┬╖  {data.RowCount} row(s)  ┬╖  {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC",
            cellFont,
            XBrushes.Gray,
            new XPoint(margin, y));
        y += 16;

        var tableWidth = page.Width.Point - (margin * 2);
        var widths = data.Columns.Select(c => Math.Max(c.Width, 80)).ToArray();
        if (widths.Length == 0)
            widths = [160];
        var widthSum = (double)widths.Sum();
        var colWidths = widths.Select(w => tableWidth * w / widthSum).ToArray();

        void EnsureSpace()
        {
            if (y + rowHeight <= page.Height.Point - margin)
                return;
            gfx.Dispose();
            page = AddPage(document, landscape);
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
        }

        void DrawCells(IReadOnlyList<string> cells, XFont font, bool header)
        {
            EnsureSpace();
            var x = margin;
            if (header)
            {
                gfx.DrawRectangle(
                    new XSolidBrush(XColor.FromArgb(90, 70, 160)),
                    x,
                    y - 10,
                    colWidths.Sum(),
                    rowHeight);
            }

            var textBrush = header ? XBrushes.White : XBrushes.Black;
            for (var i = 0; i < cells.Count && i < colWidths.Length; i++)
            {
                var rect = new XRect(x + 2, y - 8, colWidths[i] - 4, rowHeight);
                gfx.DrawString(Truncate(cells[i], 48), font, textBrush, rect, XStringFormats.CenterLeft);
                x += colWidths[i];
            }

            if (!header)
            {
                gfx.DrawLine(
                    XPens.LightGray,
                    margin,
                    y + 6,
                    margin + colWidths.Sum(),
                    y + 6);
            }

            y += rowHeight;
        }

        DrawCells(data.Columns.Select(c => c.Label).ToList(), headerFont, header: true);
        foreach (var row in data.Rows)
        {
            DrawCells(
                data.Columns.Select(c => row.TryGetValue(c.Label, out var v) ? v ?? string.Empty : string.Empty).ToList(),
                cellFont,
                header: false);
        }

        if (data.Totals is not null)
        {
            DrawCells(
                data.Columns.Select(c => data.Totals.TryGetValue(c.Label, out var v) ? v ?? string.Empty : string.Empty).ToList(),
                headerFont,
                header: true);
        }

        gfx.Dispose();
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static PdfPage AddPage(PdfDocument document, bool landscape)
    {
        var page = document.AddPage();
        page.Orientation = landscape ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
        return page;
    }

    private static string BuildCsv(ReportRunResult data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", data.Columns.Select(c => Csv(c.Label))));
        foreach (var row in data.Rows)
        {
            sb.AppendLine(string.Join(",", data.Columns.Select(c =>
                Csv(row.TryGetValue(c.Label, out var v) ? v : string.Empty))));
        }

        if (data.Totals is not null)
        {
            sb.AppendLine(string.Join(",", data.Columns.Select(c =>
                Csv(data.Totals.TryGetValue(c.Label, out var v) ? v : string.Empty))));
        }

        return sb.ToString();
    }

    private static string BuildSpreadsheetMl(ReportRunResult data)
    {
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var rows = new List<XElement>
        {
            new(ss + "Row", data.Columns.Select(c => Cell(ss, c.Label)))
        };
        foreach (var row in data.Rows)
        {
            rows.Add(new XElement(ss + "Row",
                data.Columns.Select(c => Cell(ss, row.TryGetValue(c.Label, out var v) ? v : string.Empty))));
        }

        if (data.Totals is not null)
        {
            rows.Add(new XElement(ss + "Row",
                data.Columns.Select(c => Cell(ss, data.Totals.TryGetValue(c.Label, out var v) ? v : string.Empty))));
        }

        var workbook = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(ss + "Workbook",
                new XAttribute(XNamespace.Xmlns + "ss", ss),
                new XElement(ss + "Worksheet",
                    new XAttribute(ss + "Name", Truncate(data.Name, 31)),
                    new XElement(ss + "Table", rows))));
        return workbook.ToString();
    }

    private static XElement Cell(XNamespace ss, string? value) =>
        new(ss + "Cell", new XElement(ss + "Data", new XAttribute(ss + "Type", "String"), value ?? string.Empty));

    private static string Csv(string? value)
    {
        var raw = value ?? string.Empty;
        if (raw.Contains('"') || raw.Contains(',') || raw.Contains('\n') || raw.Contains('\r'))
            return $"\"{raw.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return raw;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "report" : cleaned;
    }

    private static string Truncate(string? value, int max)
    {
        var raw = value ?? string.Empty;
        return raw.Length <= max ? raw : raw[..(max - 1)] + "ΓÇª";
    }

    private static void EnsureFonts()
    {
        try
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        }
        catch
        {
            // Font resolver already set.
        }
    }
}
