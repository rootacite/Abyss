
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Abyss.Misc;

public static class StringClusterer
{
    public static Dictionary<string, List<string>> Cluster(
    string[] inputs,
    double mergeThreshold = 0.20
)
{
    return Cluster(inputs, s => s, mergeThreshold);
}

public static Dictionary<string, List<T>> Cluster<T>(
    IEnumerable<T> inputs,
    Func<T, string> selector,
    double mergeThreshold = 0.20
)
{
    if (inputs == null) throw new ArgumentNullException(nameof(inputs));
    if (selector == null) throw new ArgumentNullException(nameof(selector));

    var items = inputs.Select(x => new Item(selector(x), x)).ToList();

    var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
    foreach (var it in items)
    {
        if (!groups.TryGetValue(it.KeyNorm, out var g))
        {
            g = new Group(it.KeyNorm);
            groups[it.KeyNorm] = g;
        }
        g.Items.Add(it);
    }

    var keys = groups.Keys.ToList();
    var uf = new UnionFind(keys.Count);
    for (int i = 0; i < keys.Count; i++)
    {
        for (int j = i + 1; j < keys.Count; j++)
        {
            string k1 = keys[i], k2 = keys[j];
            int maxLen = Math.Max(k1.Length, k2.Length);
            if (maxLen == 0) continue;
            int lenDiff = Math.Abs(k1.Length - k2.Length);
            if (lenDiff > Math.Max(2, (int)Math.Ceiling(maxLen * 0.5))) continue;

            double distNorm = (double)Levenshtein(k1, k2) / maxLen;
            if (distNorm <= mergeThreshold && CompatibleForMerge(groups[k1], groups[k2]))
            {
                uf.Union(i, j);
            }
        }
    }

    var merged = new Dictionary<int, List<Group>>();
    for (int i = 0; i < keys.Count; i++)
    {
        int root = uf.Find(i);
        if (!merged.TryGetValue(root, out var list)) 
        { 
            list = new List<Group>(); 
            merged[root] = list; 
        }
        list.Add(groups[keys[i]]);
    }

    var result = new Dictionary<string, List<T>>();
    foreach (var kv in merged)
    {
        var combinedItems = kv.Value.SelectMany(g => g.Items).ToList();
        var members = combinedItems.Select(it => it.Original).ToList();

        var uniqueMembers = new List<T>();
        var seen = new HashSet<string>();
        foreach (var it in combinedItems)
            if (seen.Add(it.Original)) uniqueMembers.Add((T)it.Payload);

        string rawPrefix = LongestCommonPrefix(members);
        string groupName = TrimToTokenBoundary(rawPrefix);
        groupName = Regex.Replace(groupName, @"[\s_\-\.]+$", "");

        result[groupName] = uniqueMembers;
    }

    return result;
}

    private static bool CompatibleForMerge(Group g1, Group g2)
    {
        if (g1.HasAnyAlphaTokenCountGreaterThanOne() != g2.HasAnyAlphaTokenCountGreaterThanOne())
            return false;

        if (g1.HasTrailingNumber() != g2.HasTrailingNumber())
            return false;

        return true;
    }

    #region Helpers & Internal Types
    private class Item
    {
        public string Original { get; }
        public string[] Tokens { get; }
        public string KeyOriginal { get; }
        public string KeyNorm { get; }
        public int AlphaTokenCount { get; }
        public bool EndsWithNumber { get; }
        public object Payload { get; }

        public Item(string original, object? payload = null)
        {
            Original = original;
            Payload = payload ?? original;
            Tokens = TokenizeAlphaNum(Original).ToArray();
            EndsWithNumber = Tokens.Length > 0 && Regex.IsMatch(Tokens.Last(), "^[0-9]+$");
            var alphaTokens = Tokens.Where(t => Regex.IsMatch(t, "^[A-Za-z]+$")).ToList();
            AlphaTokenCount = alphaTokens.Count;

            string candidate;
            if (EndsWithNumber && alphaTokens.Count >= 1)
                candidate = alphaTokens.Last();
            else if (alphaTokens.Count > 0)
                candidate = alphaTokens.OrderByDescending(t => t.Length).First();
            else if (Tokens.Length > 0)
                candidate = Tokens[0];
            else
                candidate = Original.Trim();

            KeyOriginal = candidate;
            KeyNorm = NormalizeKey(candidate);
        }
        
        
        public static IEnumerable<string> TokenizeAlphaNum(string s)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            var matches = Regex.Matches(s, @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]+|[A-Za-z]+|[0-9]+");
            foreach (Match m in matches) yield return m.Value;
        }
    }

    private class Group
    {
        public string KeyNorm { get; }
        public List<Item> Items { get; } = new List<Item>();

        public Group(string keyNorm) { KeyNorm = keyNorm; }

        public bool HasAnyAlphaTokenCountGreaterThanOne()
            => Items.Any(it => it.AlphaTokenCount > 1);

        public bool HasTrailingNumber()
            => Items.Any(it => it.EndsWithNumber);

        public string RepresentativeOriginal()
            => Items.Select(i => i.KeyOriginal).FirstOrDefault() ?? KeyNorm;
    }

    private static string NormalizeKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc == UnicodeCategory.NonSpacingMark) continue; 
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0) return m;
        if (m == 0) return n;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost
                );
            }
        }
        return d[n, m];
    }

    private class UnionFind
    {
        private int[] _p;
        public UnionFind(int n) { _p = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { return _p[x] == x ? x : (_p[x] = Find(_p[x])); }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[b] = a; }
    }
    
    private static string LongestCommonPrefix(List<string> strs)
    {
        if (strs.Count == 0) return string.Empty;
        string prefix = strs[0];
        foreach (var s in strs)
        {
            int len = Math.Min(prefix.Length, s.Length);
            int i = 0;
            while (i < len && prefix[i] == s[i]) i++;
            prefix = prefix.Substring(0, i);
            if (prefix == string.Empty) break;
        }
        return prefix;
    }
    
    private static string TrimToTokenBoundary(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return string.Empty;

        var boundary = new Regex(@"[\s0-9_\-\.]");
        int lastBoundary = -1;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (boundary.IsMatch(prefix[i].ToString()))
                lastBoundary = i;
        }

        if (lastBoundary >= 0)
        {
            return prefix.Substring(0, lastBoundary).TrimEnd();
        }

        return prefix;
    }
    #endregion
}
