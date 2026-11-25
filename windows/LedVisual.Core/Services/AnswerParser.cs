using System.Text.RegularExpressions;

namespace LedVisual.Core.Services;

public static partial class AnswerParser
{
    /// <summary>
    /// Maximum reasonable MCQ option index (prevents accidental 4-digit years or stray numbers).
    /// </summary>
    public const int MaxOptionNumber = 20;

    /// <summary>
    /// Parses raw model output into sorted, deduplicated 1-based answer indices (e.g. [3] or [1, 3]).
    /// Returns null if the output indicates failure/no question or cannot be reliably parsed.
    /// </summary>
    public static IReadOnlyList<int>? Parse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var cleaned = rawText
            .Trim()
            .Replace("`", string.Empty)
            .Replace("*", string.Empty);

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        var upper = cleaned.ToUpperInvariant();
        if (upper is "NONE" or "ERROR" or "UNKNOWN" or "N/A" or "0")
        {
            return null;
        }

        // 1. Strict full-string match of numbers separated by commas, semicolons, "and", or whitespace
        var strict = ParseStrictNumberList(cleaned);
        if (strict is not null)
        {
            return strict;
        }

        // 2. Check the last non-empty line (models occasionally echo reasoning then put the final answer last)
        var lines = cleaned
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length > 0)
        {
            var lastLine = lines[^1];
            if (!string.Equals(lastLine, cleaned, StringComparison.Ordinal))
            {
                var lastUpper = lastLine.ToUpperInvariant();
                if (lastUpper is "NONE" or "ERROR" or "UNKNOWN")
                {
                    return null;
                }

                var strictLast = ParseStrictNumberList(lastLine);
                if (strictLast is not null)
                {
                    return strictLast;
                }

                var lettersLast = ParseStrictLetterList(lastLine);
                if (lettersLast is not null)
                {
                    return lettersLast;
                }
            }
        }

        // 3. Strict letter list fallback (e.g. "B" -> [2], "A, C" -> [1, 3])
        var letters = ParseStrictLetterList(cleaned);
        if (letters is not null)
        {
            return letters;
        }

        // 4. Explicit answer phrase extraction (e.g. "Answer: 1, 3" or "Correct options are 2 and 4")
        var extracted = ParseLabeledAnswer(cleaned);
        if (extracted is not null)
        {
            return extracted;
        }

        return null;
    }

    private static IReadOnlyList<int>? ParseStrictNumberList(string text)
    {
        var normalized = AndWordRegex().Replace(text, ",").Replace("&", ",");
        if (!StrictNumberListRegex().IsMatch(normalized))
        {
            return null;
        }

        var numbers = ExtractValidNumbers(normalized);
        return numbers.Count == 0 ? null : numbers;
    }

    private static IReadOnlyList<int>? ParseStrictLetterList(string text)
    {
        var normalized = AndWordRegex().Replace(text, ",").Replace("&", ",");
        if (!StrictLetterListRegex().IsMatch(normalized))
        {
            return null;
        }

        var letters = new SortedSet<int>();
        foreach (var ch in normalized.ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'J')
            {
                letters.Add(ch - 'A' + 1);
            }
        }

        return letters.Count == 0 ? null : letters.ToArray();
    }

    private static IReadOnlyList<int>? ParseLabeledAnswer(string text)
    {
        var matches = LabeledAnswerRegex().Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        var lastMatch = matches[^1];
        if (lastMatch.Groups.Count < 2)
        {
            return null;
        }

        var captured = lastMatch.Groups[1].Value;
        var numbers = ExtractValidNumbers(captured);
        return numbers.Count == 0 ? null : numbers;
    }

    private static List<int> ExtractValidNumbers(string text)
    {
        var set = new SortedSet<int>();
        foreach (Match match in DigitsRegex().Matches(text))
        {
            if (int.TryParse(match.Value, out var val) && val >= 1 && val <= MaxOptionNumber)
            {
                set.Add(val);
            }
        }

        return [.. set];
    }

    [GeneratedRegex(@"\band\b", RegexOptions.IgnoreCase)]
    private static partial Regex AndWordRegex();

    [GeneratedRegex(@"^\s*\d+(?:\s*[,;/\s]\s*\d+)*\s*\.?\s*$")]
    private static partial Regex StrictNumberListRegex();

    [GeneratedRegex(@"^\s*[A-Ja-j](?:\s*[,;/\s]\s*[A-Ja-j])*\s*\.?\s*$")]
    private static partial Regex StrictLetterListRegex();

    [GeneratedRegex(@"(?:final\s+answer|correct\s+answers?|correct\s+options?|answers?|options?)\s*(?:is|are|:|=)?\s*([0-9][0-9,\s&and;/]*)", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledAnswerRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();
}
