namespace PdfWatermarkLambda.Models;

/// <summary>
/// Payload sent by the VitalityHub backend to invoke this Lambda.
/// The Lambda downloads the original PDF from S3, stamps "CANCELADA", uploads
/// the result to a separate S3 key, and updates the Invoice document in MongoDB.
/// </summary>
public class StampInvoicePayload
{
    /// <summary>S3 key of the original CFDI PDF (e.g. "cfdi/{companyId}/{uuid}.pdf").</summary>
    public string PdfS3Key { get; set; } = string.Empty;

    /// <summary>Invoice UUID (folio fiscal).</summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>Company GUID (string form) — used for the S3 key and MongoDB filter.</summary>
    public string CompanyId { get; set; } = string.Empty;
}
