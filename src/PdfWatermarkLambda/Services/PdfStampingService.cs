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

        // 4. Update the Invoice document in MongoDB: set cancelledPdfS3Key
        try
        {
            var collection = _db.GetCollection<BsonDocument>("Invoice");
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

        // 5. Send email to receptor with stamped PDF + acuse XML attached
        if (!string.IsNullOrEmpty(payload.ReceptorEmail) && !string.IsNullOrEmpty(payload.AcuseS3Key))
        {
            byte[]? acuseBytes = null;
            try
            {
                var acuseResponse = await _s3.GetObjectAsync(_bucketName, payload.AcuseS3Key);
                using var ms = new MemoryStream();
                await acuseResponse.ResponseStream.CopyToAsync(ms);
                acuseBytes = ms.ToArray();
            }
            catch (Exception ex)
            {
                logger.LogError($"Failed to download acuse XML for email {payload.Uuid}: {ex.Message}");
            }

            await SendCancellationEmailAsync(payload, stampedBytes, acuseBytes, logger);
        }
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

    private static string HtmlEncode(string? s)
        => (s ?? string.Empty)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
