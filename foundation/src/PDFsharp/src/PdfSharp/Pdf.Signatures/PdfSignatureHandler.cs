// PDFsharp - A .NET library for processing PDF
// See the LICENSE file in the solution root for more information.

#if !NET6_0_OR_GREATER
using System.Text;
#endif
using PdfSharp.Drawing;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Internal;
using PdfSharp.Pdf.IO;

namespace PdfSharp.Pdf.Signatures
{
    /// <summary>
    /// PdfDocument signature handler.
    /// Attaches a PKCS#7 signature digest to PdfDocument.
    /// </summary>
    // DigitalSignatureHandler rename file
    public class DigitalSignatureHandler
    {
        /// <summary>
        /// Big enough space reserved by PdfPlaceholderObject to be replaced by the actual computed value of the byte range to sign
        /// Worst case: signature dictionary is near the end of an 10 GB PDF file.
        /// </summary>
        const int ByteRangePlaceholderLength = 36; // = "[0 9999999999 9999999999 9999999999]".Length

        DigitalSignatureHandler(PdfDocument document, IDigitalSigner signer, DigitalSignatureOptions options)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Signer = signer ?? throw new ArgumentNullException(nameof(signer));
            Options = options ?? throw new ArgumentNullException(nameof(options));

            if (options.PageIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options.PageIndex),
                    "Signature page index cannot be negative.");
            }

            // TODO_OLD in document: Set document version depending on digest type from options.
        }

        /// <summary>
        /// Gets or creates the digital signature handler for the specified document.
        /// </summary>
        public static DigitalSignatureHandler ForDocument(PdfDocument document, IDigitalSigner signer, DigitalSignatureOptions options)
        {
            return document._digitalSignatureHandler ??= new(document, signer, options);
        }


        /// <summary>
        /// Gets the PDF document the signature will be attached to.
        /// </summary>
        public PdfDocument Document { get; init; }

        /// <summary>
        /// Gets the options for the digital signature.
        /// </summary>
        public DigitalSignatureOptions Options { get; init; }

        IDigitalSigner Signer { get; init; }

        internal async Task ComputeSignatureAndRange(PdfWriter writer)
        {
            var (rangedStreamToSign, byteRangeArray) = GetRangeToSignAndByteRangeArray(writer.Stream);

            Debug.Assert(_signatureFieldByteRangePlaceholder != null);
            _signatureFieldByteRangePlaceholder.WriteActualObject(byteRangeArray, writer);

            // Computing signature from document’s digest.
            var signature = await Signer.GetSignatureAsync(rangedStreamToSign).ConfigureAwait(false);

            Debug.Assert(_placeholderItem != null);
            int expectedLength = _placeholderItem.Size;
            if (signature.Length > expectedLength)
                throw new Exception($"The actual digest length {signature.Length} is larger than the approximation made {expectedLength}. Not enough room in the placeholder to fit the signature.");

            // Write the signature at the space reserved by placeholder item.
            writer.Stream.Position = _placeholderItem.StartPosition;

            // When the signature includes a timestamp, the exact length is unknown until the signature is definitely calculated.
            // Therefore, we write the angle brackets here and override the placeholder white spaces.
            writer.WriteRaw('<');
            writer.Write(PdfEncoders.RawEncoding.GetBytes(FormatHex(signature)));

            // Fill up the allocated placeholder. Signature is sometimes considered invalid if there are spaces after '>'.
            for (int x = signature.Length; x < expectedLength; ++x)
                writer.WriteRaw("00");

            writer.WriteRaw('>');
        }

        string FormatHex(byte[] bytes)  // ...use RawEncoder
        {
#if NET6_0_OR_GREATER
            return Convert.ToHexString(bytes);
#else
            var result = new StringBuilder();

            for (int idx = 0; idx < bytes.Length; idx++)
                result.AppendFormat("{0:X2}", bytes[idx]);

            return result.ToString();
#endif
        }

        /// <summary>
        /// Get the bytes ranges to sign.
        /// As recommended in PDF specs, whole document will be signed, except for the hexadecimal signature token value in the /Contents entry.
        /// Example: '/Contents &lt;aaaaa111111&gt;' => '&lt;aaaaa111111&gt;' will be excluded from the bytes to sign.
        /// </summary>
        /// <param name="stream"></param>
        (RangedStream rangedStream, PdfArray byteRangeArray) GetRangeToSignAndByteRangeArray(Stream stream)
        {
            Debug.Assert( _placeholderItem !=null, nameof(_placeholderItem) + " must not be null here.");

            SizeType firstRangeOffset = 0;
            SizeType firstRangeLength = _placeholderItem.StartPosition;
            SizeType secondRangeOffset = _placeholderItem.EndPosition;
            SizeType secondRangeLength = stream.Length - _placeholderItem.EndPosition;

            var byteRangeArray = new PdfArray();
            byteRangeArray.Elements.Add(new PdfLongInteger(firstRangeOffset));
            byteRangeArray.Elements.Add(new PdfLongInteger(firstRangeLength));
            byteRangeArray.Elements.Add(new PdfLongInteger(secondRangeOffset));
            byteRangeArray.Elements.Add(new PdfLongInteger(secondRangeLength));

            var rangedStream = new RangedStream(stream,
            [
                new(firstRangeOffset, firstRangeLength),
                new(secondRangeOffset, secondRangeLength)
            ]);

            return (rangedStream, byteRangeArray);
        }

        /// <summary>
        /// Adds the PDF objects required for a digital signature.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        internal async Task AddSignatureComponentsAsync()
        {
            if (Options.PageIndex >= Document.PageCount)
                throw new ArgumentOutOfRangeException($"Signature page doesn't exist, specified page was {Options.PageIndex + 1} but document has only {Document.PageCount} page(s).");

            var signatureSize = await Signer.GetSignatureSizeAsync().ConfigureAwait(false);
            _placeholderItem = new(signatureSize);
            _signatureFieldByteRangePlaceholder = new PdfPlaceholderObject(ByteRangePlaceholderLength);

            var signatureDictionary = GetSignatureDictionary(_placeholderItem, _signatureFieldByteRangePlaceholder);

            // One signer on several pages: build ONE field with several widget kids (one /Contents hole).
            // Only reached when the caller asked for extra placements; the single-placement path below is
            // left byte-for-byte unchanged so the existing (production) routes are unaffected.
            if (Options.AdditionalPlacements is { Count: > 0 })
            {
                AddMultiWidgetSignatureField(signatureDictionary);
                return;
            }

            var signatureField = GetSignatureField(signatureDictionary);

            var annotations = Document.Pages[Options.PageIndex].Elements.GetArray(PdfPage.Keys.Annots);
            if (annotations == null)
                Document.Pages[Options.PageIndex].Elements.Add(PdfPage.Keys.Annots, new PdfArray(Document, signatureField));
            else
                annotations.Elements.Add(signatureField);

            // acroform

            var catalog = Document.Catalog;

            if (catalog.Elements.GetObject(PdfCatalog.Keys.AcroForm) == null)
                catalog.Elements.Add(PdfCatalog.Keys.AcroForm, new PdfAcroForm(Document));

            if (!catalog.AcroForm.Elements.ContainsKey(PdfAcroForm.Keys.SigFlags))
                catalog.AcroForm.Elements.Add(PdfAcroForm.Keys.SigFlags, new PdfInteger(3));
            else
            {
                var sigFlagVersion = catalog.AcroForm.Elements.GetInteger(PdfAcroForm.Keys.SigFlags);
                if (sigFlagVersion < 3)
                    catalog.AcroForm.Elements.SetInteger(PdfAcroForm.Keys.SigFlags, 3);
            }

            if (catalog.AcroForm.Elements.GetValue(PdfAcroForm.Keys.Fields) == null)
                catalog.AcroForm.Elements.SetValue(PdfAcroForm.Keys.Fields, new PdfAcroField.PdfAcroFieldCollection(new PdfArray()));
            catalog.AcroForm.Fields.Elements.Add(signatureField);
        }

        PdfSignatureField GetSignatureField(PdfSignature2 signatureDic)
        {
            var signatureField = new PdfSignatureField(Document);

            signatureField.Elements.Add(PdfAcroField.Keys.V, signatureDic);

            // Annotation keys.
            signatureField.Elements.Add(PdfAcroField.Keys.FT, new PdfName("/Sig"));
            signatureField.Elements.Add(PdfAcroField.Keys.T, new PdfString(ChooseFieldName())); // unique per signature (required for multi-signature)
            signatureField.Elements.Add(PdfAcroField.Keys.Ff, new PdfInteger(132));
            signatureField.Elements.Add(PdfAcroField.Keys.DR, new PdfDictionary());
            signatureField.Elements.Add(PdfSignatureField.Keys.Type, new PdfName("/Annot"));
            signatureField.Elements.Add("/Subtype", new PdfName("/Widget"));
            signatureField.Elements.Add("/P", Document.Pages[Options.PageIndex]);

            signatureField.Elements.Add("/Rect", new PdfRectangle(Options.Rectangle));

            signatureField.CustomAppearanceHandler = Options.AppearanceHandler ?? new DefaultSignatureAppearanceHandler()
            {
                Location = Options.Location,
                Reason = Options.Reason,
                Signer = Signer.CertificateName
            };
            // TODO_OLD Call RenderCustomAppearance(); here.
            signatureField.PrepareForSave(); // TODO_OLD PdfSignatureField.PrepareForSave() is not triggered automatically so let's call it manually from here, but it would be better to be called automatically.

            Document.Internals.AddObject(signatureField);

            return signatureField;
        }

        /// <summary>
        /// Builds ONE signature field shown on SEVERAL pages: a parent field (/FT /Sig, /V, /T, /Ff, /Kids)
        /// plus one widget annotation per placement (/Subtype /Widget, /Rect, /P, /Parent, /AP). Every
        /// widget shares the single signature value — one /Contents hole, one /ByteRange — so this is
        /// cryptographically ONE signature (what "a person's signature on several pages" means in PDF; see
        /// PDF 2.0 §12.7.4.5: a field with multiple widget kids must NOT be merged with the widget). Only
        /// reached when the caller supplied <see cref="DigitalSignatureOptions.AdditionalPlacements"/>.
        /// </summary>
        void AddMultiWidgetSignatureField(PdfSignature2 signatureDic)
        {
            // Primary placement (from Options) first, then the extras — order fixes the widget /Kids order.
            var placements = new List<(int PageIndex, XRect Rectangle)> { (Options.PageIndex, Options.Rectangle) };
            foreach (var p in Options.AdditionalPlacements!)
            {
                placements.Add((p.PageIndex, p.Rectangle));
            }

            foreach (var (pageIndex, rectangle) in placements)
            {
                if (pageIndex < 0 || pageIndex >= Document.PageCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(Options.AdditionalPlacements),
                        $"Signature page {pageIndex + 1} doesn't exist; document has only {Document.PageCount} page(s).");
                }
                // Reject a malformed widget rectangle (non-finite or non-positive size) before it reaches the
                // /Rect, appearance /BBox and content stream, where it would produce a corrupt annotation.
                if (!IsFiniteRect(rectangle) || rectangle.Width <= 0 || rectangle.Height <= 0)
                {
                    throw new ArgumentException(
                        $"Signature placement rectangle must have finite, positive width/height (got {rectangle}).",
                        nameof(Options.AdditionalPlacements));
                }
            }

            // Parent field: carries /V + /T + /Ff, but is NOT itself a widget (no /Rect, /P, /Subtype).
            var field = new PdfSignatureField(Document);
            field.Elements.Add(PdfAcroField.Keys.V, signatureDic);
            field.Elements.Add(PdfAcroField.Keys.FT, new PdfName("/Sig"));
            field.Elements.Add(PdfAcroField.Keys.T, new PdfString(ChooseFieldName())); // unique per signature
            field.Elements.Add(PdfAcroField.Keys.Ff, new PdfInteger(132));
            Document.Internals.AddObject(field);

            var handler = Options.AppearanceHandler;
            var kids = new PdfArray(Document);
            foreach (var (pageIndex, rect) in placements)
            {
                var widget = new PdfDictionary(Document);
                widget.Elements.Add(PdfSignatureField.Keys.Type, new PdfName("/Annot"));
                widget.Elements.Add("/Subtype", new PdfName("/Widget"));
                widget.Elements.Add("/Rect", new PdfRectangle(rect));
                widget.Elements.Add("/P", Document.Pages[pageIndex]);
                widget.Elements.Add(PdfAcroField.Keys.Parent, field);
                if (handler != null)
                {
                    var ap = new PdfDictionary(Document);
                    ap.Elements["/N"] = RenderWidgetAppearance(rect, handler);
                    widget.Elements.Add("/AP", ap);
                }
                Document.Internals.AddObject(widget);
                kids.Elements.Add(widget);

                var annots = Document.Pages[pageIndex].Elements.GetArray(PdfPage.Keys.Annots);
                if (annots == null)
                {
                    Document.Pages[pageIndex].Elements.Add(PdfPage.Keys.Annots, new PdfArray(Document, widget));
                }
                else
                {
                    annots.Elements.Add(widget);
                }
            }
            field.Elements.Add(PdfAcroField.Keys.Kids, kids);

            // AcroForm plumbing — identical to the single-widget path; only the field object differs.
            var catalog = Document.Catalog;
            if (catalog.Elements.GetObject(PdfCatalog.Keys.AcroForm) == null)
            {
                catalog.Elements.Add(PdfCatalog.Keys.AcroForm, new PdfAcroForm(Document));
            }
            if (!catalog.AcroForm.Elements.ContainsKey(PdfAcroForm.Keys.SigFlags))
            {
                catalog.AcroForm.Elements.Add(PdfAcroForm.Keys.SigFlags, new PdfInteger(3));
            }
            else
            {
                var sigFlagVersion = catalog.AcroForm.Elements.GetInteger(PdfAcroForm.Keys.SigFlags);
                if (sigFlagVersion < 3)
                {
                    catalog.AcroForm.Elements.SetInteger(PdfAcroForm.Keys.SigFlags, 3);
                }
            }
            if (catalog.AcroForm.Elements.GetValue(PdfAcroForm.Keys.Fields) == null)
            {
                catalog.AcroForm.Elements.SetValue(PdfAcroForm.Keys.Fields, new PdfAcroField.PdfAcroFieldCollection(new PdfArray()));
            }
            catalog.AcroForm.Fields.Elements.Add(field);
        }

        /// <summary>True if every component of the rectangle is a finite (non-NaN, non-infinite) number.</summary>
        static bool IsFiniteRect(XRect r) =>
            !double.IsNaN(r.X) && !double.IsInfinity(r.X) && !double.IsNaN(r.Y) && !double.IsInfinity(r.Y) &&
            !double.IsNaN(r.Width) && !double.IsInfinity(r.Width) && !double.IsNaN(r.Height) && !double.IsInfinity(r.Height);

        /// <summary>
        /// Renders one widget's normal appearance (/N) form in LOCAL box coordinates and returns its
        /// reference. Mirrors <see cref="PdfSignatureField"/>.RenderCustomAppearance for a given rectangle.
        /// </summary>
        PdfReference RenderWidgetAppearance(XRect rect, IAnnotationAppearanceHandler handler)
        {
            var form = new XForm(Document, rect.Size);
            var gfx = XGraphics.FromForm(form);
            handler.DrawAppearance(gfx, new XRect(0, 0, rect.Width, rect.Height));
            form.DrawingFinished();
            var reference = form.PdfForm.Reference;
            form.PdfRenderer?.Close();
            return reference;
        }

        /// <summary>
        /// Picks a form-field name not yet used by any existing field, e.g. "Signature1", "Signature2",…
        /// PDF form fields must have unique fully-qualified names; reusing one breaks multi-signature
        /// (validators merge or reject the duplicate). Layered signatures each get a fresh name.
        /// </summary>
        string ChooseFieldName()
        {
            // Collect the names of fields already present (from an already-signed input). The AcroForm
            // property dereferences the indirect /AcroForm and throws when none exists yet (first
            // signature) — treat that as "no fields".
            var used = new HashSet<string>(StringComparer.Ordinal);
            PdfArray? fields = null;
            try { fields = Document.Catalog.AcroForm?.Elements.GetArray(PdfAcroForm.Keys.Fields); }
            catch { /* document has no AcroForm yet → first signature */ }
            if (fields != null)
            {
                for (int i = 0; i < fields.Elements.Count; i++)
                {
                    var field = fields.Elements.GetDictionary(i);
                    var name = field?.Elements.GetString(PdfAcroField.Keys.T);
                    if (!string.IsNullOrEmpty(name))
                        used.Add(name!);
                }
            }
            for (int n = 1; ; n++)
            {
                var candidate = "Signature" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!used.Contains(candidate))
                    return candidate;
            }
        }

        PdfSignature2 GetSignatureDictionary(PdfSignaturePlaceholderItem contents, PdfPlaceholderObject byteRange)
        {
            PdfSignature2 signatureDic = new(Document);

            signatureDic.Elements.Add(PdfSignatureField.Keys.Type, new PdfName("/Sig"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.Filter, new PdfName("/Adobe.PPKLite"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.SubFilter, new PdfName("/adbe.pkcs7.detached"));
            signatureDic.Elements.Add(PdfSignatureField.Keys.M, new PdfDate(DateTime.Now));

            signatureDic.Elements.Add(PdfSignatureField.Keys.Contents, contents);
            signatureDic.Elements.Add(PdfSignatureField.Keys.ByteRange, byteRange);
            signatureDic.Elements.Add(PdfSignatureField.Keys.Reason, new PdfString(Options.Reason));
            signatureDic.Elements.Add(PdfSignatureField.Keys.Location, new PdfString(Options.Location));

            var properties = new PdfDictionary(Document);
            signatureDic.Elements.Add("/Prop_Build", properties);
            var propertyItems = new PdfDictionary(Document);
            properties.Elements.Add("/App", propertyItems);
            propertyItems.Elements.Add("/Name",
                String.IsNullOrWhiteSpace(Options.AppName) ?
                new PdfName("/PDFsharp http://www.pdfsharp.net") :
                PdfName.FromString(Options.AppName));

            Document.Internals.AddObject(signatureDic);

            return signatureDic;
        }

        PdfSignaturePlaceholderItem? _placeholderItem;
        PdfPlaceholderObject? _signatureFieldByteRangePlaceholder;
    }
}
