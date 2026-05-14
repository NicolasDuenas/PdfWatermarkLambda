using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using MongoDB.Bson;
using MongoDB.Driver;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using PdfWatermarkLambda.Models;
using System.IO.Compression;

namespace PdfWatermarkLambda.Services;

/// <summary>
/// Downloads the original invoice PDF from S3, stamps a large red "CANCELADA"
/// watermark diagonally across every page, uploads the result to a separate S3 key,
/// updates the Invoice document in MongoDB, and emails the receptor with both the
/// stamped PDF and the acuse de cancelación XML attached.
/// All operations are best-effort — a failure in any step is logged but does not throw.
/// </summary>
public class PdfStampingService
{
    private readonly IMongoDatabase _db;
    private readonly IAmazonS3 _s3;
    private readonly IAmazonSimpleEmailServiceV2 _ses;
    private readonly string _bucketName;
    private readonly string _fromEmail;

    public PdfStampingService(
        IMongoDatabase db,
        IAmazonS3 s3,
        string bucketName,
        IAmazonSimpleEmailServiceV2 ses,
        string fromEmail)
    {
        _db        = db;
        _s3        = s3;
        _bucketName = bucketName;
        _ses       = ses;
        _fromEmail  = fromEmail;
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

        // 4. Download acuse and XML bytes (best-effort — shared between ZIP and email)
        byte[]? acuseBytes = null;
        byte[]? xmlBytes   = null;

        if (!string.IsNullOrEmpty(payload.AcuseS3Key))
        {
            try
            {
                var acuseResp = await _s3.GetObjectAsync(_bucketName, payload.AcuseS3Key);
                using var ms  = new MemoryStream();
                await acuseResp.ResponseStream.CopyToAsync(ms);
                acuseBytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to download acuse XML for {payload.Uuid}: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(payload.XmlS3Key))
        {
            try
            {
                var xmlResp  = await _s3.GetObjectAsync(_bucketName, payload.XmlS3Key);
                using var ms = new MemoryStream();
                await xmlResp.ResponseStream.CopyToAsync(ms);
                xmlBytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to download XML for {payload.Uuid}: {ex.Message}");
            }
        }

        // 5. Create and upload the cancelled ZIP (stamped PDF + original XML + acuse)
        string? cancelledZipKey = null;
        try
        {
            var entries = new List<(string Name, byte[] Data)>
            {
                ($"{payload.Uuid}_CANCELADA.pdf", stampedBytes)
            };
            if (xmlBytes   is not null) entries.Add(($"{payload.Uuid}.xml",       xmlBytes));
            if (acuseBytes is not null) entries.Add(($"{payload.Uuid}_acuse.xml", acuseBytes));

            var zipBytes = CreateZip(entries);
            cancelledZipKey = $"cfdi/{payload.CompanyId}/{payload.Uuid}_cancelada.zip";
            using var zipStream = new MemoryStream(zipBytes);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName  = _bucketName,
                Key         = cancelledZipKey,
                InputStream = zipStream,
                ContentType = "application/zip",
            });
            logger.LogInformation($"Uploaded cancelled ZIP to s3://{_bucketName}/{cancelledZipKey}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to create/upload cancelled ZIP for invoice {payload.Uuid}: {ex.Message}");
            cancelledZipKey = null;
        }

        // 6. Update MongoDB: cancelledPdfS3Key + cancelledZipS3Key
        try
        {
            var collection = _db.GetCollection<BsonDocument>("Invoice");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("uuid", payload.Uuid),
                Builders<BsonDocument>.Filter.Eq("companyId", payload.CompanyId));
            var update = Builders<BsonDocument>.Update.Set("cancelledPdfS3Key", cancelledKey);
            if (!string.IsNullOrEmpty(cancelledZipKey))
                update = update.Set("cancelledZipS3Key", cancelledZipKey);
            var result = await collection.UpdateOneAsync(filter, update);
            logger.LogInformation($"MongoDB update for {payload.Uuid}: matched={result.MatchedCount}, modified={result.ModifiedCount}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to update MongoDB for invoice {payload.Uuid}: {ex.Message}");
        }

        // 7. Send cancellation email to receptor
        if (!string.IsNullOrEmpty(payload.ReceptorEmail))
            await SendCancellationEmailAsync(payload, stampedBytes, acuseBytes, logger);
    }

    private async Task SendCancellationEmailAsync(
        StampInvoicePayload payload,
        byte[] cancelledPdfBytes,
        byte[]? acuseBytes,
        ILambdaLogger logger)
    {
        try
        {
            var emisor   = payload.EmisorNombre ?? payload.Uuid;
            var receptor = payload.ReceptorNombre ?? payload.ReceptorEmail!;
            var uuidShort = payload.Uuid.Length >= 8 ? payload.Uuid[..8].ToUpper() : payload.Uuid.ToUpper();

            var subject  = $"Cancelación de factura CFDI — {emisor}";
            var text     = $"Estimado/a {receptor},\n\n" +
                           $"La factura CFDI emitida por {emisor} con UUID {payload.Uuid} ha sido cancelada ante el SAT.\n\n" +
                           $"Adjunto encontrarás el PDF de la factura cancelada y el acuse de cancelación XML para actualizar tu contabilidad.\n\n" +
                           $"— ViHub";

            var html = $"""
                <div style="font-family:'Segoe UI',Arial,sans-serif;max-width:600px;margin:0 auto;background:#fff;border-radius:10px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,0.08);">
                  <div style="background:#607d8b;padding:28px 32px;text-align:center;">
                    <img src="https://www.vihub.app/logo192.png" alt="ViHub" width="56" style="border-radius:50%;border:3px solid rgba(255,255,255,0.3);margin-bottom:12px;"/>
                    <div style="font-size:22px;font-weight:700;color:#fff;">ViHub</div>
                  </div>
                  <div style="padding:32px;">
                    <h2 style="font-size:20px;color:#dc2626;margin:0 0 8px 0;">Cancelación de factura CFDI</h2>
                    <p style="font-size:15px;color:#555;margin:0 0 20px 0;">
                      Estimado/a <strong>{HtmlEncode(receptor)}</strong>,<br/>
                      La factura electrónica emitida por <strong>{HtmlEncode(emisor)}</strong> ha sido <strong>cancelada ante el SAT</strong>.
                    </p>
                    <table style="width:100%;border-collapse:collapse;font-size:14px;color:#333;margin-bottom:20px;">
                      <tr style="background:#f5f7f8;">
                        <td style="padding:10px 14px;font-weight:600;color:#607d8b;width:160px;">UUID (Folio Fiscal)</td>
                        <td style="padding:10px 14px;font-family:monospace;word-break:break-all;">{HtmlEncode(payload.Uuid)}</td>
                      </tr>
                    </table>
                    <p style="font-size:14px;color:#555;margin:0 0 8px 0;">
                      Adjunto encontrarás el PDF de la factura cancelada y el acuse de cancelación XML emitido por el SAT.<br/>
                      Consérvalo para actualizar tus registros contables.
                    </p>
                    <p style="font-size:13px;color:#999;margin:16px 0 0 0;">
                      Este mensaje fue enviado a través de <a href="https://www.vihub.app" style="color:#607d8b;text-decoration:none;">ViHub</a>.
                    </p>
                  </div>
                </div>
                """;

            var attachments = new List<(string ContentType, string Filename, string Base64Data)>
            {
                ("application/pdf", $"factura_cancelada_{uuidShort}.pdf", Convert.ToBase64String(cancelledPdfBytes))
            };

            if (acuseBytes is not null)
                attachments.Add(("application/xml", $"acuse_cancelacion_{uuidShort}.xml", Convert.ToBase64String(acuseBytes)));

            var rawMessage = BuildRawMessage(payload.ReceptorEmail!, _fromEmail, subject, text, html, attachments);

            await _ses.SendEmailAsync(new SendEmailRequest
            {
                Content = new EmailContent
                {
                    Raw = new RawMessage { Data = new MemoryStream(rawMessage) }
                }
            });

            logger.LogInformation($"Cancellation email sent to {payload.ReceptorEmail} for invoice {payload.Uuid}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to send cancellation email for {payload.Uuid}: {ex.Message}");
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

    // ---------- MIME helpers (same RFC 2047 approach as the VitalityHub backend) ----------

    private static byte[] BuildRawMessage(
        string to, string from, string subject, string text, string html,
        IReadOnlyList<(string ContentType, string Filename, string Base64Data)> attachments)
    {
        var mixedBoundary = $"=_mixed_{Guid.NewGuid():N}";
        var altBoundary   = $"=_alt_{Guid.NewGuid():N}";
        var encodedSubject = $"=?UTF-8?B?{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(subject))}?=";

        var sb = new System.Text.StringBuilder();
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append($"From: {from}\r\n");
        sb.Append($"To: {to}\r\n");
        sb.Append($"Subject: {encodedSubject}\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{mixedBoundary}\"\r\n\r\n");

        // multipart/alternative (text + html)
        sb.Append($"--{mixedBoundary}\r\n");
        sb.Append($"Content-Type: multipart/alternative; boundary=\"{altBoundary}\"\r\n\r\n");

        sb.Append($"--{altBoundary}\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Chunk76(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))));
        sb.Append("\r\n");

        sb.Append($"--{altBoundary}\r\n");
        sb.Append("Content-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Chunk76(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html))));
        sb.Append("\r\n");

        sb.Append($"--{altBoundary}--\r\n\r\n");

        // attachments
        foreach (var (contentType, filename, data) in attachments)
        {
            sb.Append($"--{mixedBoundary}\r\n");
            sb.Append($"Content-Type: {contentType}; name=\"{filename}\"\r\n");
            sb.Append($"Content-Disposition: attachment; filename=\"{filename}\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(Chunk76(data));
            sb.Append("\r\n");
        }

        sb.Append($"--{mixedBoundary}--\r\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Chunk76(string base64)
    {
        var sb = new System.Text.StringBuilder(base64.Length + base64.Length / 76 * 2);
        for (int i = 0; i < base64.Length; i += 76)
            sb.Append(base64, i, Math.Min(76, base64.Length - i)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Same as StampAndSaveAsync but for a CRP (Complemento de Recepción de Pagos).
    /// Updates the <c>InvoicePayment</c> MongoDB collection (not <c>Invoice</c>),
    /// filtering by <c>paymentUuid</c> = payload.Uuid.
    /// No cancellation email is sent (the receptor already receives the invoice cancellation email if applicable).
    /// </summary>
    public async Task StampPaymentAndSaveAsync(StampInvoicePayload payload, ILambdaLogger logger)
    {
        byte[] originalBytes;
        try
        {
            var response = await _s3.GetObjectAsync(_bucketName, payload.PdfS3Key);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms);
            originalBytes = ms.ToArray();
            logger.LogInformation($"CRP: Downloaded original PDF ({originalBytes.Length} bytes) from {payload.PdfS3Key}");
        }
        catch (Exception ex)
        {
            logger.LogError($"CRP: Failed to download PDF {payload.PdfS3Key}: {ex.Message}");
            return;
        }

        byte[] stampedBytes;
        try
        {
            stampedBytes = StampCancelled(originalBytes);
            logger.LogInformation($"CRP: Stamped CANCELADA watermark — output {stampedBytes.Length} bytes");
        }
        catch (Exception ex)
        {
            logger.LogError($"CRP: Failed to stamp PDF for payment {payload.Uuid}: {ex.Message}");
            return;
        }

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
            logger.LogInformation($"CRP: Uploaded cancelled PDF to s3://{_bucketName}/{cancelledKey}");
        }
        catch (Exception ex)
        {
            logger.LogError($"CRP: Failed to upload cancelled PDF for payment {payload.Uuid}: {ex.Message}");
            return;
        }

        byte[]? acuseBytes = null;
        byte[]? xmlBytes   = null;

        if (!string.IsNullOrEmpty(payload.AcuseS3Key))
        {
            try
            {
                var acuseResp = await _s3.GetObjectAsync(_bucketName, payload.AcuseS3Key);
                using var ms  = new MemoryStream();
                await acuseResp.ResponseStream.CopyToAsync(ms);
                acuseBytes = ms.ToArray();
            }
            catch (Exception ex) { logger.LogError($"CRP: Failed to download acuse XML: {ex.Message}"); }
        }

        if (!string.IsNullOrEmpty(payload.XmlS3Key))
        {
            try
            {
                var xmlResp = await _s3.GetObjectAsync(_bucketName, payload.XmlS3Key);
                using var ms = new MemoryStream();
                await xmlResp.ResponseStream.CopyToAsync(ms);
                xmlBytes = ms.ToArray();
            }
            catch (Exception ex) { logger.LogError($"CRP: Failed to download XML: {ex.Message}"); }
        }

        // Create cancelled ZIP for the CRP
        string? cancelledZipKey = null;
        try
        {
            var entries = new List<(string Name, byte[] Data)>
            {
                ($"{payload.Uuid}_CANCELADA.pdf", stampedBytes)
            };
            if (xmlBytes   is not null) entries.Add(($"{payload.Uuid}.xml",       xmlBytes));
            if (acuseBytes is not null) entries.Add(($"{payload.Uuid}_acuse.xml", acuseBytes));

            var zipBytes = CreateZip(entries);
            cancelledZipKey = $"cfdi/{payload.CompanyId}/{payload.Uuid}_cancelada.zip";
            using var zipStream = new MemoryStream(zipBytes);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName  = _bucketName,
                Key         = cancelledZipKey,
                InputStream = zipStream,
                ContentType = "application/zip",
            });
            logger.LogInformation($"CRP: Uploaded cancelled ZIP to s3://{_bucketName}/{cancelledZipKey}");
        }
        catch (Exception ex)
        {
            logger.LogError($"CRP: Failed to create cancelled ZIP for payment {payload.Uuid}: {ex.Message}");
            cancelledZipKey = null;
        }

        // Update InvoicePayment collection (filtering by paymentUuid, not uuid)
        try
        {
            var collection = _db.GetCollection<BsonDocument>("InvoicePayment");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("paymentUuid", payload.Uuid),
                Builders<BsonDocument>.Filter.Eq("companyId", payload.CompanyId));
            var update = Builders<BsonDocument>.Update.Set("cancelledPdfS3Key", cancelledKey);
            if (!string.IsNullOrEmpty(cancelledZipKey))
                update = update.Set("cancelledZipS3Key", cancelledZipKey);
            var result = await collection.UpdateOneAsync(filter, update);
            logger.LogInformation($"CRP: MongoDB InvoicePayment update for {payload.Uuid}: matched={result.MatchedCount}, modified={result.ModifiedCount}");
        }
        catch (Exception ex)
        {
            logger.LogError($"CRP: Failed to update InvoicePayment MongoDB for {payload.Uuid}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the initial invoice ZIP (PDF + XML) from S3 and stores its key in MongoDB.
    /// Called immediately after invoice generation — no watermarking.
    /// </summary>
    public async Task GenerateInvoiceZipAsync(StampInvoicePayload payload, ILambdaLogger logger)
    {
        if (string.IsNullOrEmpty(payload.PdfS3Key) || string.IsNullOrEmpty(payload.XmlS3Key))
        {
            logger.LogError($"GenerateInvoiceZip: PdfS3Key or XmlS3Key missing for invoice {payload.Uuid}.");
            return;
        }

        // 1. Download PDF and XML from S3
        byte[] pdfBytes;
        byte[] xmlBytes;

        try
        {
            var pdfResp = await _s3.GetObjectAsync(_bucketName, payload.PdfS3Key);
            using var ms = new MemoryStream();
            await pdfResp.ResponseStream.CopyToAsync(ms);
            pdfBytes = ms.ToArray();
            logger.LogInformation($"Downloaded PDF ({pdfBytes.Length} bytes) for initial ZIP of {payload.Uuid}.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to download PDF {payload.PdfS3Key} for initial ZIP: {ex.Message}");
            return;
        }

        try
        {
            var xmlResp = await _s3.GetObjectAsync(_bucketName, payload.XmlS3Key);
            using var ms = new MemoryStream();
            await xmlResp.ResponseStream.CopyToAsync(ms);
            xmlBytes = ms.ToArray();
            logger.LogInformation($"Downloaded XML ({xmlBytes.Length} bytes) for initial ZIP of {payload.Uuid}.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to download XML {payload.XmlS3Key} for initial ZIP: {ex.Message}");
            return;
        }

        // 2. Create ZIP in memory
        var zipBytes = CreateZip(new[]
        {
            ($"{payload.Uuid}.pdf", pdfBytes),
            ($"{payload.Uuid}.xml", xmlBytes),
        });

        // 3. Upload ZIP to S3
        var zipKey = $"cfdi/{payload.CompanyId}/{payload.Uuid}.zip";
        try
        {
            using var zipStream = new MemoryStream(zipBytes);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName  = _bucketName,
                Key         = zipKey,
                InputStream = zipStream,
                ContentType = "application/zip",
            });
            logger.LogInformation($"Uploaded initial invoice ZIP to s3://{_bucketName}/{zipKey}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to upload initial ZIP for invoice {payload.Uuid}: {ex.Message}");
            return;
        }

        // 4. Update MongoDB: set zipS3Key
        try
        {
            var collection = _db.GetCollection<BsonDocument>("Invoice");
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("uuid", payload.Uuid),
                Builders<BsonDocument>.Filter.Eq("companyId", payload.CompanyId));
            var update = Builders<BsonDocument>.Update.Set("zipS3Key", zipKey);
            var result = await collection.UpdateOneAsync(filter, update);
            logger.LogInformation($"MongoDB zipS3Key update for {payload.Uuid}: matched={result.MatchedCount}, modified={result.ModifiedCount}");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to update MongoDB zipS3Key for invoice {payload.Uuid}: {ex.Message}");
        }
    }

    private static byte[] CreateZip(IEnumerable<(string Name, byte[] Data)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    private static string HtmlEncode(string? s)
        => (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
