using PdfSharp.Fonts;

namespace PdfWatermarkLambda.Fonts;

/// <summary>
/// Custom PdfSharp font resolver for AWS Lambda (no system fonts available).
/// Maps any requested bold/regular font to the bundled DejaVuSans-Bold TTF.
/// The TTF is embedded as a resource so no filesystem access is needed.
/// </summary>
public class LambdaFontResolver : IFontResolver
{
    // The face name this resolver exposes
    private const string FaceName = "DejaVuSans-Bold";

    // Resource name follows: {DefaultNamespace}.{Folder}.{FileName}
    private const string ResourceName = "PdfWatermarkLambda.Fonts.DejaVuSans-Bold.ttf";

    private static readonly byte[] FontBytes = LoadFont();

    private static byte[] LoadFont()
    {
        var assembly = typeof(LambdaFontResolver).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded font resource '{ResourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Maps any family (Arial, Helvetica, etc.) to our single bundled bold face.
    /// </summary>
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(FaceName);

    /// <summary>
    /// Returns the raw TTF bytes for the resolved face name.
    /// </summary>
    public byte[]? GetFont(string faceName)
        => faceName == FaceName ? FontBytes : null;
}
