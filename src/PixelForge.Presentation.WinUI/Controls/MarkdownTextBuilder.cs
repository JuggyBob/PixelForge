using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.UI.Text;

namespace PixelForge.Presentation.Controls;

/// <summary>
/// Small, intentionally limited Markdown renderer for untrusted GitHub release
/// notes. It never interprets raw HTML and never loads remote content.
/// </summary>
public static class MarkdownTextBuilder
{
    private static readonly Regex InlinePattern = new(
        @"(\*\*[^*\r\n]+\*\*|`[^`\r\n]+`|https?://[^\s)\]>]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RichTextBlock Build(string? markdown)
    {
        var block = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            AddParagraph(block, "No release notes were provided.", italic: true);
            return block;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inCode = false;
        var inList = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                AddParagraph(block, line, monospace: true);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                inList = false;
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                var text = trimmed[level..].Trim();
                AddParagraph(block, text, bold: true, fontSize: level switch { 1 => 20, 2 => 17, _ => 15 });
                inList = false;
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                inList = true;
                AddParagraph(block, "• " + trimmed[2..], indent: 12);
                continue;
            }

            if (trimmed.Length > 2 && char.IsDigit(trimmed[0]) &&
                trimmed.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4)
            {
                inList = true;
                AddParagraph(block, trimmed, indent: 12);
                continue;
            }

            AddParagraph(block, trimmed, indent: inList ? 12 : 0);
        }

        return block;
    }

    private static void AddParagraph(RichTextBlock block, string text, bool bold = false, bool italic = false,
        bool monospace = false, double indent = 0, double fontSize = 14)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(indent, 2, 0, 4),
            LineHeight = fontSize * 1.45
        };
        AddInlines(paragraph, text, bold, italic, monospace, fontSize);
        block.Blocks.Add(paragraph);
    }

    private static void AddInlines(Paragraph paragraph, string text, bool bold, bool italic, bool monospace,
        double fontSize)
    {
        if (monospace)
        {
            paragraph.Inlines.Add(new Run
            {
                Text = text,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = Math.Max(12, fontSize - 1)
            });
            return;
        }

        var index = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > index)
                paragraph.Inlines.Add(CreateRun(text[index..match.Index], bold, italic, fontSize));

            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) && token.EndsWith("**", StringComparison.Ordinal))
            {
                paragraph.Inlines.Add(CreateRun(token[2..^2], true, italic, fontSize));
            }
            else if (token.StartsWith('`') && token.EndsWith('`'))
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = token[1..^1],
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = Math.Max(12, fontSize - 1)
                });
            }
            else if (Uri.TryCreate(token, UriKind.Absolute, out var uri) &&
                     uri.Scheme is "http" or "https")
            {
                var link = new Hyperlink { NavigateUri = uri };
                link.Inlines.Add(new Run { Text = token, FontSize = fontSize });
                paragraph.Inlines.Add(link);
            }
            else
            {
                paragraph.Inlines.Add(CreateRun(token, bold, italic, fontSize));
            }

            index = match.Index + match.Length;
        }

        if (index < text.Length)
            paragraph.Inlines.Add(CreateRun(text[index..], bold, italic, fontSize));
    }

    private static Run CreateRun(string text, bool bold, bool italic, double fontSize) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal
    };
}
