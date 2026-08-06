using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>Stamps a PNG/JPEG signature image onto a PDF page (coordinates from top-left, points).</summary>
internal static class PdfSignatureStampService
{
    public static byte[] StampSignature(
        Stream pdfInput,
        int pageNumber,
        double x,
        double y,
        double width,
        double height,
        byte[] signatureImageBytes)
    {
        if (pageNumber < 1)
            throw new ArgumentException("PageNumber must be >= 1.");
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Signature width and height must be positive.");
        if (signatureImageBytes.Length == 0)
            throw new ArgumentException("Signature image is required.");

        using var inputCopy = new MemoryStream();
        pdfInput.CopyTo(inputCopy);
        inputCopy.Position = 0;

        using var document = PdfReader.Open(inputCopy, PdfDocumentOpenMode.Modify);
        if (pageNumber > document.PageCount)
            throw new ArgumentException($"PageNumber {pageNumber} is out of range (document has {document.PageCount} page(s)).");

        var page = document.Pages[pageNumber - 1];
        using var gfx = XGraphics.FromPdfPage(page);

        using var imageStream = new MemoryStream(signatureImageBytes);
        using var image = XImage.FromStream(imageStream);

        // FE sends top-left origin; PdfSharp XGraphics also uses top-left for DrawImage.
        var drawX = x;
        var drawY = y;
        var pageWidth = page.Width.Point;
        var pageHeight = page.Height.Point;

        // Clamp into page bounds.
        if (drawX < 0) drawX = 0;
        if (drawY < 0) drawY = 0;
        if (drawX + width > pageWidth) drawX = Math.Max(0, pageWidth - width);
        if (drawY + height > pageHeight) drawY = Math.Max(0, pageHeight - height);

        gfx.DrawImage(image, drawX, drawY, width, height);

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    public static byte[] DecodeSignatureImage(string signatureImageBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureImageBase64))
            throw new ArgumentException("SignatureImageBase64 is required.");

        var raw = signatureImageBase64.Trim();
        var comma = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            raw = raw[(comma + 1)..];

        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("SignatureImageBase64 is not valid base64.", ex);
        }
    }
}
