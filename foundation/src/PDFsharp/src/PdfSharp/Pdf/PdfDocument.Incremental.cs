using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfSharp.Pdf
{
    partial class PdfDocument
    {
        /// <summary>
        /// Saves the document as an APPEND-ONLY incremental update: the original file bytes are
        /// written verbatim, then only the new/modified objects, then an incremental cross-reference
        /// section chained via /Prev. Because no existing byte is touched, a prior digital signature
        /// stays valid and further signatures can be layered one by one.
        ///
        /// Intended for adding a (visible) digital signature — requires a signature handler on this
        /// document (see <c>DigitalSignatureHandler.ForDocument</c>). Unlike the normal Save,
        /// it does NOT call PrepareForSave (whose Renumber() would destroy the original object
        /// numbering the incremental xref relies on).
        /// </summary>
        /// <param name="stream">Destination stream.</param>
        /// <param name="originalBytes">The exact bytes this document was opened from.</param>
        public async Task SaveIncrementalAsync(Stream stream, byte[] originalBytes)
        {
            if (_digitalSignatureHandler == null)
                throw new InvalidOperationException("SaveIncrementalAsync requires a digital signature handler.");
            if (originalBytes is null || originalBytes.Length == 0)
                throw new ArgumentException("originalBytes must contain the source PDF.", nameof(originalBytes));

            var origMax = IrefTable.MaxObjectNumber;
            var prevStartxref = ParseLastStartxref(originalBytes);

            // Create the signature field + appearance. New objects are numbered > origMax; the signed
            // page, the AcroForm and the Catalog get modified in place.
            await _digitalSignatureHandler.AddSignatureComponentsAsync().ConfigureAwait(false);

            // Minimal prepare only — NOT PrepareForSave() (it Compact()+Renumber()s everything).
            _fontTable?.PrepareForSave();
            Catalog.PrepareForSave();

            var writer = new PdfWriter(stream, this, null);
            try
            {
                // 1) the original revision, byte-for-byte
                writer.Stream.Write(originalBytes, 0, originalBytes.Length);
                if (originalBytes[^1] != (byte)'\n')
                    writer.WriteRaw("\n");

                // 2) the changed set: every new object + the modified originals
                var changed = new List<PdfReference>();
                var seen = new HashSet<int>();
                void Add(PdfReference? r)
                {
                    if (r is not null && r.ObjectNumber > 0 && seen.Add(r.ObjectNumber))
                        changed.Add(r);
                }
                foreach (var r in IrefTable.AllReferences)
                    if (r.ObjectNumber > origMax)
                        Add(r);
                Add(Catalog.Reference);
                Add(AcroForm.Reference);
                Add(Pages[_digitalSignatureHandler.Options.PageIndex].Reference);
                // A multi-widget signature also modifies the /Annots of EVERY page carrying an additional
                // placement — those pages must be in the changed set too, or their new widget would be an
                // orphan object no page references (invisible in viewers). Dedup is handled by Add().
                if (_digitalSignatureHandler.Options.AdditionalPlacements is { } extraPlacements)
                    foreach (var placement in extraPlacements)
                        Add(Pages[placement.PageIndex].Reference);

                // 3) append the changed objects, recording their new byte positions
                foreach (var iref in changed)
                {
                    iref.Position = writer.Position;
                    iref.Value.WriteObject(writer);
                }

                // 4) incremental cross-reference section + trailer chained via /Prev
                var startxref = writer.Position;
                var rootNumber = Catalog.ReferenceNotNull.ObjectNumber;
                var size = Math.Max(origMax, changed.Max(r => r.ObjectNumber)) + 1;

                var sb = new StringBuilder("xref\n");
                foreach (var r in changed.OrderBy(r => r.ObjectNumber))
                    sb.Append($"{r.ObjectNumber} 1\n{(long)r.Position:D10} 00000 n \n");
                sb.Append($"trailer\n<< /Size {size} /Root {rootNumber} 0 R /Prev {prevStartxref} >>\n");
                sb.Append($"startxref\n{startxref}\n%%EOF\n");
                writer.WriteRaw(sb.ToString());

                // 5) fill /ByteRange + /Contents. The range spans the whole file except the /Contents
                //    hole; the original bytes are untouched, so any prior signature remains valid.
                await _digitalSignatureHandler.ComputeSignatureAndRange(writer).ConfigureAwait(false);
            }
            finally
            {
                writer.Stream.Flush();
                _state |= DocumentState.Saved;
            }
        }

        static long ParseLastStartxref(byte[] pdf)
        {
            var window = Math.Min(2048, pdf.Length);
            var tail = Encoding.Latin1.GetString(pdf, pdf.Length - window, window);
            var m = Regex.Matches(tail, @"startxref\s+(\d+)");
            if (m.Count == 0)
                throw new InvalidOperationException("No startxref found in the original PDF (classic xref expected).");
            return long.Parse(m[^1].Groups[1].Value);
        }
    }
}
