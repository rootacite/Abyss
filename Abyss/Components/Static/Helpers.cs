
namespace Abyss.Components.Static;

public static class Helpers
{
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
        
        return PathType.NotFound;
    }
}

public enum PathType
{
    File,
    Directory,
    NotFound,
    AccessDenied
}