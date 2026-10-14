using System.Text;

namespace CameraSpeechMatch;

public static class SpeechTranscriptComparer
{
    public static SpeechTranscriptComparison Compare(string expectedText, string transcribedText)
    {
        string normalizedExpected = NormalizeForTextComparison(expectedText);
        string normalizedTranscript = NormalizeForTextComparison(transcribedText);

        bool isSame = string.Equals(normalizedExpected, normalizedTranscript, StringComparison.Ordinal);
        if (isSame)
        {
            return new SpeechTranscriptComparison(true, 100);
        }

        if (normalizedExpected.Length == 0 || normalizedTranscript.Length == 0)
        {
            return new SpeechTranscriptComparison(false, 0);
        }

        string[] expectedWords = normalizedExpected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] transcriptWords = normalizedTranscript.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        double similarity = expectedWords.Length == 1 && transcriptWords.Length == 1
            ? CalculateCharacterSimilarity(normalizedExpected, normalizedTranscript)
            : CalculateSimilarity(expectedWords, transcriptWords);

        return new SpeechTranscriptComparison(false, similarity);
    }

    private static string NormalizeForTextComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value
            .Normalize(NormalizationForm.FormKC)
            .FixPersianChars()
            .ToEnglishNumbers()
            .ToLowerInvariant();

        var result = new StringBuilder(normalized.Length);
        bool hasPendingSeparator = false;

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (hasPendingSeparator && result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(rune.ToString());
                hasPendingSeparator = false;
            }
            else
            {
                hasPendingSeparator = result.Length > 0;
            }
        }

        return result.ToString();
    }

    private static string FixPersianChars(this string value)
    {
        return value
            .Replace("ي", "ی", StringComparison.Ordinal)
            .Replace("ى", "ی", StringComparison.Ordinal)
            .Replace("ك", "ک", StringComparison.Ordinal)
            .Replace("ﮎ", "ک", StringComparison.Ordinal)
            .Replace("ﮏ", "ک", StringComparison.Ordinal)
            .Replace("ﮐ", "ک", StringComparison.Ordinal)
            .Replace("ﮑ", "ک", StringComparison.Ordinal)
            .Replace("ھ", "ه", StringComparison.Ordinal)
            .Replace('\u00A0', ' ');
    }

    private static string ToEnglishNumbers(this string value)
    {
        return value
            .Replace("۰", "0", StringComparison.Ordinal)
            .Replace("۱", "1", StringComparison.Ordinal)
            .Replace("۲", "2", StringComparison.Ordinal)
            .Replace("۳", "3", StringComparison.Ordinal)
            .Replace("۴", "4", StringComparison.Ordinal)
            .Replace("۵", "5", StringComparison.Ordinal)
            .Replace("۶", "6", StringComparison.Ordinal)
            .Replace("۷", "7", StringComparison.Ordinal)
            .Replace("۸", "8", StringComparison.Ordinal)
            .Replace("۹", "9", StringComparison.Ordinal)
            .Replace("٠", "0", StringComparison.Ordinal)
            .Replace("١", "1", StringComparison.Ordinal)
            .Replace("٢", "2", StringComparison.Ordinal)
            .Replace("٣", "3", StringComparison.Ordinal)
            .Replace("٤", "4", StringComparison.Ordinal)
            .Replace("٥", "5", StringComparison.Ordinal)
            .Replace("٦", "6", StringComparison.Ordinal)
            .Replace("٧", "7", StringComparison.Ordinal)
            .Replace("٨", "8", StringComparison.Ordinal)
            .Replace("٩", "9", StringComparison.Ordinal);
    }

    private static double CalculateCharacterSimilarity(string expected, string transcript)
    {
        string[] expectedCharacters = expected.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        string[] transcriptCharacters = transcript.EnumerateRunes().Select(rune => rune.ToString()).ToArray();

        return CalculateSimilarity(expectedCharacters, transcriptCharacters);
    }

    private static double CalculateSimilarity(IReadOnlyList<string> expected, IReadOnlyList<string> transcript)
    {
        int longestLength = Math.Max(expected.Count, transcript.Count);
        if (longestLength == 0)
        {
            return 100;
        }

        int distance = CalculateLevenshteinDistance(expected, transcript);
        double percentage = (1d - (double)distance / longestLength) * 100d;

        return Math.Round(Math.Max(0, percentage), 2, MidpointRounding.AwayFromZero);
    }

    private static int CalculateLevenshteinDistance(IReadOnlyList<string> source, IReadOnlyList<string> target)
    {
        if (source.Count > target.Count)
        {
            return CalculateLevenshteinDistance(target, source);
        }

        int[] previous = new int[source.Count + 1];
        int[] current = new int[source.Count + 1];

        for (int sourceIndex = 0; sourceIndex <= source.Count; sourceIndex++)
        {
            previous[sourceIndex] = sourceIndex;
        }

        for (int targetIndex = 1; targetIndex <= target.Count; targetIndex++)
        {
            current[0] = targetIndex;

            for (int sourceIndex = 1; sourceIndex <= source.Count; sourceIndex++)
            {
                int substitutionCost = string.Equals(
                    source[sourceIndex - 1],
                    target[targetIndex - 1],
                    StringComparison.Ordinal)
                    ? 0
                    : 1;

                current[sourceIndex] = Math.Min(
                    Math.Min(
                        current[sourceIndex - 1] + 1,
                        previous[sourceIndex] + 1),
                    previous[sourceIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[source.Count];
    }
}

public sealed record SpeechTranscriptComparison(bool IsSame, double SimilarityPercent);
