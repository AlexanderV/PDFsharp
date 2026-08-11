// PDFsharp - A .NET library for processing PDF
// See the LICENSE file in the solution root for more information.

#if WPF
using System.IO;
#endif
using FluentAssertions;
using PdfSharp.Diagnostics;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Forms;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;
#if CORE
using PdfSharp.Fonts;
using PdfSharp.Quality;
#endif
using Xunit;

namespace PdfSharp.Tests.Pdf.Signatures
{
    [Collection("PDFsharp")]
    public class MultiWidgetSignatureTests : IDisposable
    {
        public MultiWidgetSignatureTests()
        {
            PdfSharpCore.ResetAll();
#if CORE
            GlobalFontSettings.FontResolver = new UnitTestFontResolver();
#endif
        }

        public void Dispose()
        {
            PdfSharpCore.ResetAll();
        }

        [Fact]
        public void Signature_without_additional_placements_is_one_merged_field()
        {
            var signedBytes = Sign(pageCount: 2, additionalPlacements: null);

            using var document = PdfReader.Open(new MemoryStream(signedBytes), PdfDocumentOpenMode.Import);
            var fields = RootFieldsOf(document);
            fields.Elements.Count.Should().Be(1);

            // The field is the widget annotation, so it has no kids and carries /Rect itself.
            var field = fields.Elements.GetRequiredDictionary(0);
            field.Elements.ContainsKey(PdfFormField.Keys.Kids).Should().BeFalse();
            field.Elements.GetName(PdfAnnotation.Keys.Subtype).Should().Be("/Widget");
            field.Elements.GetRectangle(PdfAnnotation.Keys.Rect).Should().NotBeNull();

            AnnotationCountOf(document, 0).Should().Be(1);
            AnnotationCountOf(document, 1).Should().Be(0);
        }

        [Fact]
        public void Signature_with_additional_placements_is_one_field_with_one_widget_per_placement()
        {
            var signedBytes = Sign(pageCount: 3,
            [
                new PdfSignaturePlacement { PageIndex = 1, Rectangle = new XRect(36, 100, 200, 50) },
                new PdfSignaturePlacement { PageIndex = 2, Rectangle = new XRect(36, 200, 200, 50) }
            ]);

            using var document = PdfReader.Open(new MemoryStream(signedBytes), PdfDocumentOpenMode.Import);
            var fields = RootFieldsOf(document);
            fields.Elements.Count.Should().Be(1, "several placements are one signature, not several signatures");

            var field = fields.Elements.GetRequiredDictionary(0);
            field.Elements.GetName(PdfFormField.Keys.FT).Should().Be("/Sig");
            field.Elements.GetString(PdfFormField.Keys.T).Should().Be("Signature1");

            // A field with several widget annotations must not be merged with them, so it has no /Rect
            // and no /Subtype of its own.
            field.Elements.ContainsKey(PdfAnnotation.Keys.Rect).Should().BeFalse();
            field.Elements.ContainsKey(PdfAnnotation.Keys.Subtype).Should().BeFalse();

            var kids = field.Elements.GetArray(PdfFormField.Keys.Kids);
            kids.Should().NotBeNull();
            kids!.Elements.Count.Should().Be(3);

            for (int idx = 0; idx < kids.Elements.Count; idx++)
            {
                var widget = kids.Elements.GetRequiredDictionary(idx);
                widget.Elements.GetName(PdfAnnotation.Keys.Subtype).Should().Be("/Widget");
                widget.Elements.GetRectangle(PdfAnnotation.Keys.Rect).Should().NotBeNull();
                widget.Elements.GetDictionary(PdfAnnotation.Keys.AP).Should().NotBeNull("every widget is drawn");

                // Every widget refers to the one field, which holds the one signature value.
                widget.Elements.GetReference(PdfFormField.Keys.Parent)!.ObjectID
                    .Should().Be(field.ReferenceNotNull.ObjectID);
            }

            // One widget per page, each page refers to its own widget.
            AnnotationCountOf(document, 0).Should().Be(1);
            AnnotationCountOf(document, 1).Should().Be(1);
            AnnotationCountOf(document, 2).Should().Be(1);
        }

        [Fact]
        public void Signature_with_additional_placements_has_one_signature_value()
        {
            var signedBytes = Sign(pageCount: 2,
                [new PdfSignaturePlacement { PageIndex = 1, Rectangle = new XRect(36, 100, 200, 50) }]);

            var text = TextOf(signedBytes);
            CountOf(text, "/ByteRange").Should().Be(1, "several widgets share one signature");
            CountOf(text, "/adbe.pkcs7.detached").Should().Be(1);
        }

        [Fact]
        public void Signature_with_additional_placements_writes_every_affected_page_of_an_incremental_update()
        {
            using var stream = new MemoryStream();
            using (var newDocument = new PdfDocument())
            {
                for (int idx = 0; idx < 3; idx++)
                    newDocument.AddPage();
                newDocument.Save(stream, false);
            }

            byte[] signedBytes;
            using (var document = PdfReader.Open(new MemoryStream(stream.ToArray()), PdfDocumentOpenMode.ModifyIncremental))
            {
                _ = DigitalSignatureHandler.ForDocument(document, new TestSigner(), new DigitalSignatureOptions
                {
                    Rectangle = new XRect(36, 36, 200, 50),
                    AppearanceHandler = new EmptyAppearanceHandler(),
                    AdditionalPlacements =
                    [
                        new PdfSignaturePlacement { PageIndex = 1, Rectangle = new XRect(36, 100, 200, 50) },
                        new PdfSignaturePlacement { PageIndex = 2, Rectangle = new XRect(36, 200, 200, 50) }
                    ]
                });

                using var signedStream = new MemoryStream();
                document.SaveIncremental(signedStream);
                signedBytes = signedStream.ToArray();
            }

            // Every page that gets a widget is modified, so the incremental update must write it again.
            // Otherwise, the widget is an annotation no page refers to and no viewer shows it.
            using var signedDocument = PdfReader.Open(new MemoryStream(signedBytes), PdfDocumentOpenMode.Import);
            AnnotationCountOf(signedDocument, 0).Should().Be(1);
            AnnotationCountOf(signedDocument, 1).Should().Be(1);
            AnnotationCountOf(signedDocument, 2).Should().Be(1);
        }

        [Fact]
        public void Signature_placement_can_have_an_appearance_handler_of_its_own()
        {
            var handlerOfOptions = new RecordingAppearanceHandler();
            var handlerOfPlacement = new RecordingAppearanceHandler();

            var primaryRectangle = new XRect(36, 36, 200, 50);
            var otherRectangle = new XRect(72, 100, 120, 30);

            using var document = new PdfDocument();
            document.AddPage();
            document.AddPage();
            _ = DigitalSignatureHandler.ForDocument(document, new TestSigner(), new DigitalSignatureOptions
            {
                Rectangle = primaryRectangle,
                AppearanceHandler = handlerOfOptions,
                AdditionalPlacements =
                [
                    new PdfSignaturePlacement
                    {
                        PageIndex = 1,
                        Rectangle = otherRectangle,
                        AppearanceHandler = handlerOfPlacement
                    }
                ]
            });
            using var stream = new MemoryStream();
            document.Save(stream, false);

            // The handler of the options draws the places that have no handler of their own.
            handlerOfOptions.Rectangles.Should().Equal(primaryRectangle);
            handlerOfPlacement.Rectangles.Should().Equal(otherRectangle);
        }

        [Fact]
        public void Signature_placement_on_a_page_that_does_not_exist_is_rejected()
        {
            Action sign = () => Sign(pageCount: 1,
                [new PdfSignaturePlacement { PageIndex = 7, Rectangle = new XRect(36, 36, 200, 50) }]);

            sign.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*specified page was 8*");
        }

        [Fact]
        public void Signature_placement_with_an_invalid_rectangle_is_rejected()
        {
            Action sign = () => Sign(pageCount: 2,
                [new PdfSignaturePlacement { PageIndex = 1, Rectangle = new XRect(36, 36, Double.NaN, 50) }]);

            sign.Should().Throw<ArgumentException>().WithMessage("*finite numbers*");
        }

        static byte[] Sign(int pageCount, IReadOnlyList<PdfSignaturePlacement>? additionalPlacements)
        {
            using var document = new PdfDocument();
            for (int idx = 0; idx < pageCount; idx++)
                document.AddPage();

            var options = new DigitalSignatureOptions
            {
                ContactInfo = "John Doe",
                Location = "Seattle",
                Reason = "License Agreement",
                Rectangle = new XRect(36, 36, 200, 50),
                AppearanceHandler = new EmptyAppearanceHandler(),
                AdditionalPlacements = additionalPlacements
            };
            _ = DigitalSignatureHandler.ForDocument(document, new TestSigner(), options);

            using var stream = new MemoryStream();
            document.Save(stream, false);
            return stream.ToArray();
        }

        static PdfArray RootFieldsOf(PdfDocument document)
        {
            var fields = document.Catalog.GetAcroForm()?.Elements.GetArray(PdfForm.Keys.Fields);
            fields.Should().NotBeNull();
            return fields!;
        }

        static int AnnotationCountOf(PdfDocument document, int pageIndex)
            => document.Pages[pageIndex].Elements.GetArray(PdfPage.Keys.Annots)?.Elements.Count ?? 0;

        static string TextOf(byte[] pdfBytes)
        {
            var chars = new char[pdfBytes.Length];
            for (int idx = 0; idx < pdfBytes.Length; idx++)
                chars[idx] = (char)pdfBytes[idx];
            return new String(chars);
        }

        /// <summary>
        /// An appearance handler that draws nothing, but records the rectangles it was called with.
        /// </summary>
        class RecordingAppearanceHandler : IAnnotationAppearanceHandler
        {
            public List<XRect> Rectangles { get; } = [];

            public void DrawAppearance(XGraphics gfx, XRect rect) => Rectangles.Add(rect);
        }

        static int CountOf(string text, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
