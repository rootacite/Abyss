
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace Abyss.Components.Static;

public static class Helpers
{
    private static readonly FileExtensionContentTypeProvider _provider = InitProvider();

    private static FileExtensionContentTypeProvider InitProvider()
    {
        var provider = new FileExtensionContentTypeProvider();

        provider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
        provider.Mappings[".ts"]   = "video/mp2t";
        provider.Mappings[".mpd"]  = "application/dash+xml";

        return provider;
    }

    public static string GetContentType(string path)
    {
        if (_provider.TryGetContentType(path, out var contentType))
        {
            return contentType;
        }

        return "application/octet-stream";
    }
    
    public static string? SafePathCombine(string basePath, params string[] pathParts)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return null;
    
        if (basePath.Contains("..") || pathParts.Any(p => p.Contains("..")))
            return null;
    
        string combinedPath = Path.Combine(basePath, Path.Combine(pathParts));
        string fullPath = Path.GetFullPath(combinedPath);

        if (!fullPath.StartsWith(Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
            return null;
    
        return fullPath;
    }
    
    public static PathType GetPathType(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                return PathType.Directory;
            }
            else
            {
                return PathType.File;
            }
        }
        catch (FileNotFoundException)
        {
            return PathType.NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return PathType.NotFound;
        }
        catch (UnauthorizedAccessException)
        {
            return PathType.AccessDenied;
        }
    }
}

public enum PathType
{
    File,
    Directory,
    NotFound,
    AccessDenied
}


public static class StringArrayExtensions
{
    public static string[] SortLikeWindows(this string[] array)
    {
        if (array.Length == 0) return array;
        
        Array.Sort(array, new WindowsFileNameComparer());
        return array;
    }

    public static string[] SortLikeWindowsDescending(this string[] array)
    {
        if (array.Length == 0) return array;
        
        Array.Sort(array, new WindowsFileNameComparerDescending());
        return array;
    }

    public static void SortLikeWindowsInPlace(this string[] array)
    {
        if (array.Length == 0) return;
        
        Array.Sort(array, new WindowsFileNameComparer());
    }

    public static void SortLikeWindowsDescendingInPlace(this string[] array)
    {
        if (array.Length == 0) return;
        
        Array.Sort(array, new WindowsFileNameComparerDescending());
    }
}

public class WindowsFileNameComparer : IComparer<string>
{
    private static readonly Regex Regex = new Regex(@"(\d+|\D+)", RegexOptions.Compiled);
    private static readonly CompareInfo CompareInfo = CultureInfo.InvariantCulture.CompareInfo;
    
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;
        if (ReferenceEquals(x, y)) return 0;
        
        var partsX = Regex.Matches(x);
        var partsY = Regex.Matches(y);
        
        int minLength = Math.Min(partsX.Count, partsY.Count);
        
        for (int i = 0; i < minLength; i++)
        {
            string partX = partsX[i].Value;
            string partY = partsY[i].Value;
            
            if (long.TryParse(partX, out long numX) && long.TryParse(partY, out long numY))
            {
                int comparison = numX.CompareTo(numY);
                if (comparison != 0) return comparison;
            }
            else
            {
                int comparison;
                if (ContainsChinese(partX) || ContainsChinese(partY))
                {
                    comparison = CompareInfo.Compare(partX, partY, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                }
                else
                {
                    comparison = string.Compare(partX, partY, StringComparison.OrdinalIgnoreCase);
                }
                
                if (comparison != 0) return comparison;
            }
        }
        
        return partsX.Count.CompareTo(partsY.Count);
    }

    private static bool ContainsChinese(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
                return true;
            if (c >= 0x3400 && c <= 0x4DBF)
                return true;
        }
        return false;
    }
}

public class WindowsFileNameComparerDescending : IComparer<string>
{
    private static readonly WindowsFileNameComparer AscendingComparer = new WindowsFileNameComparer();
    
    public int Compare(string? x, string? y)
    {
        return AscendingComparer.Compare(y, x);
    }
}

public static class StringNaturalCompare
{
    private static readonly WindowsFileNameComparer Comparer = new WindowsFileNameComparer();
    
    public static int Compare(string x, string y)
    {
        return Comparer.Compare(x, y);
    }
}