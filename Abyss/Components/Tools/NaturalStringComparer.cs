namespace Abyss.Components.Tools;

public static class NaturalSortExtensions
{
    public static IOrderedEnumerable<T> NaturalSort<T>(this IEnumerable<T> source, Func<T, string> keySelector)
    {
        return source.OrderBy(keySelector, new NaturalStringComparer());
    }
}

public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? a, string? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int aIndex = 0;
        int bIndex = 0;

        while (aIndex < a.Length && bIndex < b.Length)
        {
            if (char.IsDigit(a[aIndex]) && char.IsDigit(b[bIndex]))
            {
                long aNum = 0;
                long bNum = 0;

                while (aIndex < a.Length && char.IsDigit(a[aIndex]))
                {
                    aNum = aNum * 10 + (a[aIndex] - '0');
                    aIndex++;
                }

                while (bIndex < b.Length && char.IsDigit(b[bIndex]))
                {
                    bNum = bNum * 10 + (b[bIndex] - '0');
                    bIndex++;
                }

                if (aNum != bNum)
                {
                    return aNum.CompareTo(bNum);
                }
            }
            else
            {
                int charCompare = a[aIndex].CompareTo(b[bIndex]);
                if (charCompare != 0)
                {
                    return charCompare;
                }
                aIndex++;
                bIndex++;
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}