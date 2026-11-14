using System.Text;

namespace PixelForge.Engines.OCR;

internal sealed record OcrPageLines(int PageNumber, int Width, int Height, IReadOnlyList<OcrTextLine> Lines);

internal static class OcrMarkdownBuilder
{
    private const string PageBreak = """
```{=openxml}
<w:p><w:r><w:br w:type="page"/></w:r></w:p>
```
""";

    public static string Build(IReadOnlyList<OcrPageLines> pages)
    {
        var sb = new StringBuilder();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var page = pages[pageIndex];
            if (pageIndex > 0) sb.AppendLine().AppendLine(PageBreak).AppendLine();
            AppendPage(sb, page);
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void AppendPage(StringBuilder sb, OcrPageLines page)
    {
        if (page.Lines.Count == 0)
        {
            sb.AppendLine("<!-- No readable text was detected on this page. -->");
            return;
        }

        var medianHeight = page.Lines.Select(l => l.Height).OrderBy(h => h).ElementAt(page.Lines.Count / 2);
        var orderedLines = OrderForReading(page);
        var rows = GroupRows(orderedLines, medianHeight);
        var consumed = new bool[rows.Count];
        var paragraph = new List<string>();
        string? previous = null;
        double? previousBottom = null;
        var previousColumn = int.MinValue;
        var previousWasBullet = false;

        for (var i = 0; i < rows.Count; i++)
        {
            if (consumed[i]) continue;
            if (rows[i].Count >= 2)
            {
                var candidates = new List<List<OcrTextLine>> { rows[i] };
                var j = i + 1;
                while (j < rows.Count && rows[j].Count >= 2 && rows[j].Count == rows[i].Count)
                {
                    candidates.Add(rows[j]);
                    j++;
                }

                var gaps = candidates[0].Zip(candidates[0].Skip(1), (a, b) => b.X - a.Right).ToList();
                if (candidates.Count >= 2 && gaps.Count > 0 && gaps.Min() >= medianHeight * 1.2)
                {
                    FlushParagraph(sb, paragraph);
                    if (previousWasBullet) sb.AppendLine();
                    AppendTable(sb, candidates);
                    for (var k = i; k < j; k++) consumed[k] = true;
                    previous = null;
                    previousBottom = null;
                    previousWasBullet = false;
                    i = j - 1;
                    continue;
                }
            }

            var row = rows[i];
            var column = row.Max(l => l.Column);
            if (previousColumn != int.MinValue && column != previousColumn)
            {
                FlushParagraph(sb, paragraph);
                previous = null;
                previousBottom = null;
            }
            previousColumn = column;

            var text = string.Join(" ", row.OrderBy(l => l.X).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
            if (text.Length == 0) continue;
            text = NormalizeLine(text);
            var top = row.Min(l => l.Y);
            var height = row.Max(l => l.Height);
            var heading = height >= medianHeight * 1.28 && text.Length <= 120 && LooksLikeHeading(text);
            var bullet = TryBullet(text, out var bulletText);
            if (heading)
            {
                FlushParagraph(sb, paragraph);
                if (previousWasBullet) sb.AppendLine();
                sb.AppendLine((height >= medianHeight * 1.65 ? "# " : "## ") + Escape(text));
                sb.AppendLine();
                previous = null;
                previousBottom = null;
                previousWasBullet = false;
                continue;
            }

            if (bullet)
            {
                FlushParagraph(sb, paragraph);
                sb.AppendLine("- " + Escape(bulletText));
                previous = bulletText;
                previousBottom = top + height;
                previousWasBullet = true;
                continue;
            }

            var gap = previousBottom is null ? double.MaxValue : top - previousBottom.Value;
            if (previous is not null && gap <= medianHeight * 1.05)
            {
                paragraph.Add(text);
                previousWasBullet = false;
            }
            else
            {
                FlushParagraph(sb, paragraph);
                if (previousWasBullet) sb.AppendLine();
                previousWasBullet = false;
                paragraph.Add(text);
            }

            previous = text;
            previousBottom = top + height;
        }

        FlushParagraph(sb, paragraph);
    }

    private static void AppendTable(StringBuilder sb, IReadOnlyList<List<OcrTextLine>> rows)
    {
        var columns = rows.Max(r => r.Count);
        sb.Append('|');
        for (var c = 0; c < columns; c++)
            sb.Append(' ').Append(Escape(CellText(rows[0], c))).Append(" |");
        sb.AppendLine();
        sb.Append('|');
        for (var c = 0; c < columns; c++) sb.Append("---|");
        sb.AppendLine();
        for (var r = 1; r < rows.Count; r++)
        {
            sb.Append('|');
            for (var c = 0; c < columns; c++)
                sb.Append(' ').Append(Escape(CellText(rows[r], c))).Append(" |");
            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static string CellText(List<OcrTextLine> row, int index) =>
        index < row.Count ? row.OrderBy(l => l.X).ElementAt(index).Text : "";

    private static void FlushParagraph(StringBuilder sb, List<string> paragraph)
    {
        if (paragraph.Count == 0) return;
        sb.AppendLine(Escape(string.Join(" ", paragraph)));
        sb.AppendLine();
        paragraph.Clear();
    }

    private static IReadOnlyList<OcrTextLine> OrderForReading(OcrPageLines page)
    {
        var width = page.Width > 0 ? page.Width : page.Lines.Max(l => l.Right);
        if (width <= 0) return page.Lines.OrderBy(l => l.CenterY).ThenBy(l => l.X).ToList();

        var barriers = page.Lines
            .Where(l => l.Width >= width * 0.62)
            .OrderBy(l => l.CenterY)
            .ToList();
        if (barriers.Count == 0) return OrderSegment(page.Lines, width);

        var result = new List<OcrTextLine>();
        var cursorY = double.NegativeInfinity;
        foreach (var barrier in barriers)
        {
            var segment = page.Lines.Where(l =>
                !ReferenceEquals(l, barrier) && l.CenterY >= cursorY && l.CenterY < barrier.Y).ToList();
            result.AddRange(OrderSegment(segment, width));
            result.Add(barrier with { Column = 0 });
            cursorY = barrier.Bottom;
        }

        result.AddRange(OrderSegment(page.Lines.Where(l => !barriers.Contains(l) && l.CenterY >= cursorY).ToList(), width));
        return result;
    }

    private static IReadOnlyList<OcrTextLine> OrderSegment(IReadOnlyList<OcrTextLine> lines, double width)
    {
        if (lines.Count < 4) return lines.OrderBy(l => l.CenterY).ThenBy(l => l.X).ToList();

        var occupancy = new bool[Math.Max(1, (int)Math.Ceiling(width))];
        foreach (var line in lines)
        {
            var start = Math.Clamp((int)Math.Floor(line.X), 0, occupancy.Length - 1);
            var end = Math.Clamp((int)Math.Ceiling(line.Right), start + 1, occupancy.Length);
            for (var x = start; x < end; x++) occupancy[x] = true;
        }

        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;
        for (var x = 0; x < occupancy.Length; x++)
        {
            if (!occupancy[x])
            {
                if (runStart < 0) runStart = x;
                continue;
            }

            if (runStart >= 0)
            {
                var length = x - runStart;
                if (length > bestLength) { bestLength = length; bestStart = runStart; }
                runStart = -1;
            }
        }

        if (runStart >= 0 && occupancy.Length - runStart > bestLength)
        {
            bestLength = occupancy.Length - runStart;
            bestStart = runStart;
        }

        var minGap = Math.Max(12, width * 0.035);
        if (bestStart < width * 0.12 || bestStart + bestLength > width * 0.88 ||
            bestLength < minGap || width <= 0)
            return lines.OrderBy(l => l.CenterY).ThenBy(l => l.X).ToList();

        var split = bestStart + bestLength / 2.0;
        var left = lines.Where(l => l.CenterX < split).OrderBy(l => l.CenterY).ThenBy(l => l.X)
            .Select(l => l with { Column = 1 }).ToList();
        var right = lines.Where(l => l.CenterX >= split).OrderBy(l => l.CenterY).ThenBy(l => l.X)
            .Select(l => l with { Column = 2 }).ToList();
        if (left.Count < 2 || right.Count < 2)
            return lines.OrderBy(l => l.CenterY).ThenBy(l => l.X).ToList();
        return left.Concat(right).ToList();
    }

    private static List<List<OcrTextLine>> GroupRows(IReadOnlyList<OcrTextLine> lines, double medianHeight)
    {
        var rows = new List<List<OcrTextLine>>();
        foreach (var line in lines)
        {
            var row = rows.LastOrDefault();
            if (row is not null)
            {
                var center = row.Average(l => l.CenterY);
                if (Math.Abs(center - line.CenterY) <= Math.Max(4, medianHeight * 0.55))
                {
                    row.Add(line);
                    continue;
                }
            }

            rows.Add([line]);
        }

        foreach (var row in rows) row.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    private static bool LooksLikeHeading(string text)
    {
        if (text.Length == 0) return false;
        var letters = text.Count(char.IsLetter);
        var upper = text.Count(char.IsUpper);
        return letters > 0 && (upper >= letters * 0.6 || text.Length <= 80);
    }

    private static bool TryBullet(string text, out string content)
    {
        var prefixes = new[] { "• ", "· ", "▪ ", "◦ ", "‣ ", "- ", "* " };
        foreach (var prefix in prefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            content = text[prefix.Length..].Trim();
            return content.Length > 0;
        }

        var dot = text.IndexOf(". ", StringComparison.Ordinal);
        if (dot is > 0 and <= 3 && text[..dot].All(char.IsDigit))
        {
            content = text[(dot + 2)..].Trim();
            return content.Length > 0;
        }

        content = text;
        return false;
    }

    private static string NormalizeLine(string text) =>
        string.Join(" ", text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("[", "\\[")
            .Replace("]", "\\]")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
}
