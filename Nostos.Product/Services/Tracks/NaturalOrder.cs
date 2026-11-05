namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Orders names the way a person reads them: runs of digits compare as
/// numbers, so "2 - Intro" comes before "10 - End". Ties fall back to
/// ordinal comparison, which keeps the order total and repeatable.
/// </summary>
public static class NaturalOrder
{
    public static int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var startA = i;
                var startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var runA = a.AsSpan(startA, i - startA).TrimStart('0');
                var runB = b.AsSpan(startB, j - startB).TrimStart('0');
                if (runA.Length != runB.Length)
                    return runA.Length.CompareTo(runB.Length);

                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0)
                    return digits;
                continue;
            }

            var left = char.ToUpperInvariant(a[i]);
            var right = char.ToUpperInvariant(b[j]);
            if (left != right)
                return left.CompareTo(right);
            i++;
            j++;
        }

        var remaining = (a.Length - i).CompareTo(b.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(a, b);
    }
}
