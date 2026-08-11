// PDFsharp - A .NET library for processing PDF
// See the LICENSE file in the solution root for more information.

using PdfSharp.Drawing;
using PdfSharp.Pdf.Annotations;

// v7.0.0 Ready

namespace PdfSharp.Pdf.Signatures
{
    /// <summary>
    /// Sets the options of the DigitalSignatureHandler class.
    /// </summary>
    public class DigitalSignatureOptions()
    {
        /// <summary>
        /// Gets or sets the appearance handler that draws the visual representation of the signature in the PDF.
        /// </summary>
        public IAnnotationAppearanceHandler? AppearanceHandler { get; init; }

        /// <summary>
        /// Gets or sets a string associated with the signature.
        /// </summary>
        public string ContactInfo { get; init; } = "";

        /// <summary>
        /// Gets or sets a string associated with the signature.
        /// </summary>
        public string Location { get; init; } = "";

        /// <summary>
        /// Gets or sets a string associated with the signature.
        /// </summary>
        public string Reason { get; init; } = "";

        /// <summary>
        /// Gets or sets the name of the application used to sign the document.
        /// </summary>
        public string AppName { get; init; } = "PDFsharp http://www.pdfsharp.com";

        /// <summary>
        /// The location of the visual representation on the selected page.
        /// </summary>
        public XRect Rectangle { get; init; }

        /// <summary>
        /// The page zero-based index of the page showing the signature.
        /// </summary>
        public int PageIndex { get; init; }

        /// <summary>
        /// Gets or sets additional places showing the signature, besides <see cref="Rectangle"/> on
        /// <see cref="PageIndex"/>. If null or empty, the signature is shown once.
        /// Otherwise, the signature becomes one field with one widget annotation per place, which shows
        /// the very same signature on several places or pages. There is still only one signature, i.e.
        /// one /Contents entry and one /ByteRange entry.
        /// </summary>
        public IReadOnlyList<PdfSignaturePlacement>? AdditionalPlacements { get; init; }
    }

    /// <summary>
    /// Defines one place showing the visual representation of a digital signature.
    /// See <see cref="DigitalSignatureOptions.AdditionalPlacements"/>.
    /// </summary>
    public sealed class PdfSignaturePlacement
    {
        /// <summary>
        /// The page zero-based index of the page showing the signature.
        /// </summary>
        public int PageIndex { get; init; }

        /// <summary>
        /// The location of the visual representation on that page.
        /// </summary>
        public XRect Rectangle { get; init; }

        /// <summary>
        /// Gets or sets the appearance handler that draws the visual representation of the signature at
        /// this place. If null, the appearance handler of the
        /// <see cref="DigitalSignatureOptions.AppearanceHandler"/> is used, so that all places show the
        /// same representation. Set it to show e.g. a smaller representation on the following pages.
        /// </summary>
        public IAnnotationAppearanceHandler? AppearanceHandler { get; init; }
    }
}
