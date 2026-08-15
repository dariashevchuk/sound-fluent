using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SoundFluent.Services;

public enum DiffOp
{
    Same,
    Removed,
    Added
}

public sealed record DiffPart(DiffOp Op, string Word);

/// <summary>
/// Word-level longest-common-subsequence diff. Messages are short, so the O(n*m)
/// table is free, and word granularity is what you want for Polish — a changed
/// case ending should light up the whole word, not a single letter.
/// </summary>
public static class WordDiff
{
    public static List<DiffPart> Compute(string before, string after)
    {
        string[] a = Tokenize(before);
        string[] b = Tokenize(after);

        int n = a.Length, m = b.Length;
        var lcs = new int[n + 1, m + 1];

        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = Equal(a[i], b[j])
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var parts = new List<DiffPart>();
        int x = 0, y = 0;

        while (x < n && y < m)
        {
            if (Equal(a[x], b[y]))
            {
                parts.Add(new DiffPart(DiffOp.Same, b[y]));
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                parts.Add(new DiffPart(DiffOp.Removed, a[x]));
                x++;
            }
            else
            {
                parts.Add(new DiffPart(DiffOp.Added, b[y]));
                y++;
            }
        }

        while (x < n) parts.Add(new DiffPart(DiffOp.Removed, a[x++]));
        while (y < m) parts.Add(new DiffPart(DiffOp.Added, b[y++]));

        return parts;
    }

    private static string[] Tokenize(string text) =>
        Regex.Split(text.Trim(), @"\s+", RegexOptions.None, TimeSpan.FromSeconds(2));

    // Case-insensitive so a capitalisation fix doesn't strike the whole word,
    // but culture-invariant so Polish diacritics compare as themselves.
    private static bool Equal(string left, string right) =>
        string.Equals(left, right, StringComparison.InvariantCultureIgnoreCase);
}
