using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SaaSApp.Repository.Infrastructure.Services;

/// <summary>
/// Stamps a signature image onto a PDF page.
/// FE coordinates: top-left of the <b>upright displayed</b> page, in PDF points.
/// Handles page <c>/Rotate</c> (common on Arabic scans) so the stamp matches the viewer.
/// </summary>
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

        var prepared = PrepareSignatureImage(signatureImageBytes);

        using var inputCopy = new MemoryStream();
        pdfInput.CopyTo(inputCopy);
        inputCopy.Position = 0;

        using var document = PdfReader.Open(inputCopy, PdfDocumentOpenMode.Modify);
        if (document.Version < 14)
            document.Version = 14;

        if (pageNumber > document.PageCount)
            throw new ArgumentException($"PageNumber {pageNumber} is out of range (document has {document.PageCount} page(s)).");

        var page = document.Pages[pageNumber - 1];
        using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

        using var imageStream = new MemoryStream(prepared);
        using var image = XImage.FromStream(imageStream);

        DrawSignatureOnPage(gfx, page, image, x, y, width, height);

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

    private static void DrawSignatureOnPage(
        XGraphics gfx,
        PdfPage page,
        XImage image,
        double x,
        double y,
        double width,
        double height)
    {
        var mediaW = page.Width.Point;
        var mediaH = page.Height.Point;
        var rotate = NormalizeRotate(page.Rotate);

        // FE / pdf.js coordinates are for the upright displayed page.
        var (dispW, dispH) = DisplaySize(mediaW, mediaH, rotate);

        if (width > dispW) width = dispW;
        if (height > dispH) height = dispH;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        if (x + width > dispW) x = Math.Max(0, dispW - width);
        if (y + height > dispH) y = Math.Max(0, dispH - height);

        if (rotate == 0)
        {
            gfx.DrawImage(image, x, y, width, height);
            return;
        }

        // Map display-space center → MediaBox (PdfSharp top-left), then counter-rotate
        // so after the viewer applies page.Rotate the signature reads upright.
        var (cx, cy) = DisplayPointToMedia(x + width / 2.0, y + height / 2.0, mediaW, mediaH, rotate);

        gfx.Save();
        gfx.TranslateTransform(cx, cy);
        gfx.RotateTransform(-rotate);
        gfx.DrawImage(image, -width / 2.0, -height / 2.0, width, height);
        gfx.Restore();
    }

    private static int NormalizeRotate(int rotate) =>
        ((rotate % 360) + 360) % 360;

    private static (double Width, double Height) DisplaySize(double mediaW, double mediaH, int rotate) =>
        rotate is 90 or 270 ? (mediaH, mediaW) : (mediaW, mediaH);

    /// <summary>
    /// Convert a point from upright display (top-left) into PdfSharp MediaBox (top-left).
    /// PDF /Rotate is clockwise; display size is (mediaH, mediaW) for 90/270.
    /// </summary>
    private static (double X, double Y) DisplayPointToMedia(
        double dx,
        double dy,
        double mediaW,
        double mediaH,
        int rotate) =>
        rotate switch
        {
            // Display TL = media BL → (mx, my) = (dy, mediaH - dx)
            90 => (dy, mediaH - dx),
            180 => (mediaW - dx, mediaH - dy),
            // Display TL = media TR → (mx, my) = (mediaW - dy, dx)
            270 => (mediaW - dy, dx),
            _ => (dx, dy)
        };

    private static byte[] PrepareSignatureImage(byte[] signatureImageBytes)
    {
        try
        {
            using var image = Image.Load<Rgba32>(signatureImageBytes);
            TrimTransparentOrNearWhite(image);
            image.Mutate(ctx => ctx.BackgroundColor(Color.White));

            using var ms = new MemoryStream();
            image.Save(ms, new PngEncoder
            {
                ColorType = PngColorType.Rgb,
                CompressionLevel = PngCompressionLevel.BestSpeed
            });
            return ms.ToArray();
        }
        catch (UnknownImageFormatException)
        {
            return signatureImageBytes;
        }
        catch (ImageFormatException)
        {
            return signatureImageBytes;
        }
    }

    private static void TrimTransparentOrNearWhite(Image<Rgba32> image)
    {
        var minX = image.Width;
        var minY = image.Height;
        var maxX = -1;
        var maxY = -1;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ref readonly var px = ref row[x];
                    if (!IsInkPixel(px))
                        continue;

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        });

        if (maxX < minX || maxY < minY)
            return;

        const int pad = 2;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(image.Width - 1, maxX + pad);
        maxY = Math.Min(image.Height - 1, maxY + pad);

        var cropW = maxX - minX + 1;
        var cropH = maxY - minY + 1;
        if (cropW >= image.Width && cropH >= image.Height)
            return;

        image.Mutate(ctx => ctx.Crop(new Rectangle(minX, minY, cropW, cropH)));
    }

    private static bool IsInkPixel(Rgba32 px)
    {
        if (px.A < 16)
            return false;

        if (px.R > 245 && px.G > 245 && px.B > 245 && px.A > 200)
            return false;

        return true;
    }
}
