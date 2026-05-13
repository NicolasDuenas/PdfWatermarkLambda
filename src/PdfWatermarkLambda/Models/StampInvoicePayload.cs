namespace PdfWatermarkLambda.Models;

/// <summary>
/// Payload sent by the VitalityHub backend to invoke this Lambda.
/// </summary>
public class StampInvoicePayload
{
    /// <summary>
    /// Controls what the Lambda does:
    /// <list type="bullet">
    ///   <item><c>null</c> / <c>"WatermarkAndZip"</c> — stamp "CANCELADA" on the PDF, create the cancelled ZIP, send cancellation email.</item>
    ///   <item><c>"GenerateInvoiceZip"</c> — create the initial invoice ZIP (PDF + XML) after invoice generation; no watermarking.</item>
    /// </list>
    /// </summary>
    public string? Action { get; set; }

    /// <summary>S3 key of the original CFDI PDF (e.g. "cfdi/{companyId}/{uuid}.pdf").</summary>
    public string PdfS3Key { get; set; } = string.Empty;

    /// <summary>S3 key of the original CFDI XML. Required for GenerateInvoiceZip; used in the cancelled ZIP for WatermarkAndZip.</summary>
    public string? XmlS3Key { get; set; }

    /// <summary>Invoice UUID (folio fiscal).</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>Company GUID (string form) — used for the S3 key and MongoDB filter.</summary>
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>S3 key of the acuse de cancelación XML. When set, it is attached to the email and included in the cancelled ZIP.</summary>
    public string? AcuseS3Key { get; set; }

    /// <summary>Receptor email address. When set, sends the cancellation email.</summary>
    public string? ReceptorEmail { get; set; }

    /// <summary>Receptor display name (for the email greeting).</summary>
    public string? ReceptorNombre { get; set; }

    /// <summary>Emisor display name (clinic / doctor legal name).</summary>
    public string? EmisorNombre { get; set; }
}
