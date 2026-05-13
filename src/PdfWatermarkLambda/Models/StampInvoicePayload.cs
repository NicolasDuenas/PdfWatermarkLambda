namespace PdfWatermarkLambda.Models;

/// <summary>
/// Payload sent by the VitalityHub backend to invoke this Lambda.
/// The Lambda downloads the original PDF from S3, stamps "CANCELADA", uploads
/// the result to a separate S3 key, and sends the complete cancellation email
/// (acuse XML + cancelled PDF) to the receptor.
/// </summary>
public class StampInvoicePayload
{
    /// <summary>S3 key of the original CFDI PDF (e.g. "cfdi/{companyId}/{uuid}.pdf").</summary>
    public string PdfS3Key { get; set; } = string.Empty;

    /// <summary>Invoice UUID (folio fiscal).</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>Company GUID (string form) — used for the S3 key and MongoDB filter.</summary>
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>S3 key of the acuse de cancelación XML. When set, it is attached to the email.</summary>
    public string? AcuseS3Key { get; set; }

    /// <summary>Receptor email address. When set, sends the cancellation email.</summary>
    public string? ReceptorEmail { get; set; }

    /// <summary>Receptor display name (for the email greeting).</summary>
    public string? ReceptorNombre { get; set; }

    /// <summary>Emisor display name (clinic / doctor legal name).</summary>
    public string? EmisorNombre { get; set; }
}
