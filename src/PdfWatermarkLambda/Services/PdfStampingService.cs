using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using MongoDB.Bson;
using MongoDB.Driver;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfWatermarkLambda.Models;

namespace PdfWatermarkLambda.Services;

/// <summary>
/// Downloads the original invoice PDF from S3, stamps a large red "CANCELADA"
/// watermark diagonally across every page, uploads the result to a separate S3 key,
/// and updates the Invoice document in MongoDB with the new key.
/// All operations are best-effort — a failure in any step is logged but does not throw.
/// </summary>
public class PdfStampingService
{
    private readonly IMongoDatabase _db;
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;

    public PdfStampingService(IMongoDatabase db, IAmazonS3 s3, string bucketName)
    {
        _db = db;
        _s3 = s3;
        _bucketName = bucketName;
    }

    public async Task StampAndSaveAsync(StampInvoicePayload payload, ILambdaLogger logger)
    {
        // 1. Download the original PDF from S3
        byte[] originalBytes;
        try
        {
            var response = await _s3.GetObjectAsync(_bucketName, payload.PdfS3Key);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms);
            originalBytes = ms.ToArray();
            logger.LogInformation($"Downloaded original PDF ({originalBytes.Length} bytes) from {payload.PdfS3Key}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to download PDF {payload.PdfS3Key}: {ex.Message}");
            return;
        }

        // 2. Stamp "CANCELADA" across every page
        byte[] stampedBytes;
        try
        {
            stampedBytes = StampCancelled(originalBytes);
            logger.LogInformation($"Stamped CANCELADA watermark — output {stampedBytes.Length} bytes");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to stamp PDF for invoice {payload.Uuid}: {ex.Message}");
            return;
        }

        // 3. Upload the stamped PDF to a separate S3 key (original PdfS3Key is NEVER overwritten)
        var cancelledKey = $"cfdi/{payload.CompanyId}/{payload.Uuid}_cancelada.pdf";
        try
        {
            using var pdfStream = new MemoryStream(stampedBytes);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName  = _bucketName,
                Key         = cancelledKey,
                InputStream = pdfStream,
                ContentType = "application/pdf",
            });
            logger.LogInformation($"Uploaded cancelled PDF to s3://{_bucketName}/{cancelledKey}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to upload cancelled PDF for invoice {payload.Uuid}: {ex.Message}");
            return;
        }

        // 4. Update the Invoice document in MongoDB: set cancelledPdfS3Key
        try
        {
            var collection = _db.GetCollection<BsonDocument>("Invoice");

            // uuid and companyId are stored as plain strings (GuidSerializer + camelCase convention)
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("uuid", payload.Uuid),
                Builders<BsonDocument>.Filter.Eq("companyId", payload.CompanyId));

            var update = Builders<BsonDocument>.Update.Set("cancelledPdfS3Key", cancelledKey);

            var result = await collection.UpdateOneAsync(filter, update);
            logger.LogInformation($"MongoDB update for {payload.Uuid}: matched={result.MatchedCount}, modified={result.ModifiedCount}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to update MongoDB for invoice {payload.Uuid}: {ex.Message}");
        }
    }

    private static byte[] StampCancelled(byte[] pdfBytes)
    {
        using var inputStream = new MemoryStream(pdfBytes);
        using var document    = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

        var font  = new XFont("Arial", 82, XFontStyleEx.Bold);
        // Semi-transparent red: alpha=100, RGB=DC2626
        var brush = new XSolidBrush(XColor.FromArgb(100, 220, 38, 38));

        foreach (var page in document.Pages)
        {
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            gfx.RotateAtTransform(-45, new XPoint(page.Width / 2, page.Height / 2));
            gfx.DrawString(
                "CANCELADA",
                font,
                brush,
                new XRect(0, 0, page.Width, page.Height),
                XStringFormats.Center);
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }
}
