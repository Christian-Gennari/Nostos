using System.Text;

namespace Nostos.Product.BookText;

public static class BookTextChunker
{
    public static IReadOnlyList<BookTextIndexedChunk> Chunk(
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextArtifactBlock> blocks,
        int targetChars,
        int maxChars,
        int overlapChars)
    {
        targetChars = Math.Max(256, targetChars);
        maxChars = Math.Max(targetChars, maxChars);
        overlapChars = Math.Clamp(overlapChars, 0, targetChars / 2);

        var chunks = new List<BookTextIndexedChunk>();
        var currentText = new StringBuilder();
        var currentSegments = new List<BookTextSourceSegment>();
        IReadOnlyList<string> currentHeadings = [];

        void Flush()
        {
            var text = currentText.ToString().Trim();
            if (text.Length == 0)
            {
                currentText.Clear();
                currentSegments.Clear();
                return;
            }

            var ordinal = chunks.Count;
            chunks.Add(new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, ordinal),
                revision.BookId,
                revision.SourceSha256,
                revision.ExtractorVersion,
                revision.Format,
                ordinal,
                text,
                currentHeadings.ToArray(),
                currentSegments.ToArray()));

            currentText.Clear();
            currentSegments.Clear();
        }

        foreach (var block in blocks.OrderBy(block => block.Order))
        {
            var normalized = BookTextNormalization.Normalize(block.Text);
            if (normalized.Length == 0)
                continue;

            var pieces = SplitLongBlock(normalized, maxChars, overlapChars);
            foreach (var piece in pieces)
            {
                if (currentText.Length > 0
                    && (!currentHeadings.SequenceEqual(block.HeadingPath)
                        || currentText.Length + 2 + piece.Text.Length > targetChars))
                {
                    Flush();
                }

                if (currentText.Length > 0)
                    currentText.Append("\n\n");

                var baseOffset = currentText.Length;
                currentText.Append(piece.Text);
                currentHeadings = block.HeadingPath;

                var pieceStart = piece.SourceStart;
                var pieceEnd = piece.SourceStart + piece.SourceLength;
                foreach (var segment in block.SourceSegments)
                {
                    var segmentStart = segment.TextStart;
                    var segmentEnd = segment.TextStart + segment.TextLength;
                    var overlapStart = Math.Max(pieceStart, segmentStart);
                    var overlapEnd = Math.Min(pieceEnd, segmentEnd);
                    if (overlapStart >= overlapEnd)
                        continue;

                    var overlapLength = overlapEnd - overlapStart;
                    var sourceDelta = overlapStart - segmentStart;
                    currentSegments.Add(new BookTextSourceSegment(
                        TextStart: baseOffset + overlapStart - pieceStart,
                        TextLength: overlapLength,
                        Locator: SliceLocator(segment.Locator, sourceDelta, overlapLength)));
                }

                if (currentText.Length >= maxChars)
                    Flush();
            }
        }

        Flush();
        return chunks;
    }

    private static IReadOnlyList<(string Text, int SourceStart, int SourceLength)> SplitLongBlock(
        string text,
        int maxChars,
        int overlapChars)
    {
        if (text.Length <= maxChars)
            return [(text, 0, text.Length)];

        var result = new List<(string Text, int SourceStart, int SourceLength)>();
        var start = 0;
        while (start < text.Length)
        {
            var remaining = text.Length - start;
            var take = Math.Min(maxChars, remaining);
            if (take < remaining)
            {
                var window = text.AsSpan(start, take);
                var lastBreak = window.LastIndexOfAny(' ', '\n', '\t');
                if (lastBreak > maxChars / 2)
                    take = lastBreak;
            }

            var raw = text.Substring(start, take);
            var leading = 0;
            while (leading < raw.Length && char.IsWhiteSpace(raw[leading])) leading++;
            var trailing = raw.Length;
            while (trailing > leading && char.IsWhiteSpace(raw[trailing - 1])) trailing--;

            if (trailing > leading)
            {
                var pieceStart = start + leading;
                var pieceLength = trailing - leading;
                result.Add((raw.Substring(leading, pieceLength), pieceStart, pieceLength));
            }

            if (start + take >= text.Length)
                break;

            start += Math.Max(1, take - overlapChars);
        }

        return result;
    }

    private static BookTextSourceLocator SliceLocator(
        BookTextSourceLocator locator,
        int sourceDelta,
        int length) =>
        locator switch
        {
            PdfBookTextSourceLocator pdf => pdf with
            {
                StartTextOffset = pdf.StartTextOffset is { } start ? start + sourceDelta : null,
                EndTextOffset = pdf.StartTextOffset is { } pdfStart
                    ? pdfStart + sourceDelta + length
                    : pdf.EndTextOffset,
            },
            EpubBookTextSourceLocator epub => epub with
            {
                StartTextOffset = epub.StartTextOffset is { } start ? start + sourceDelta : null,
                EndTextOffset = epub.StartTextOffset is { } epubStart
                    ? epubStart + sourceDelta + length
                    : epub.EndTextOffset,
            },
            _ => locator,
        };
}
