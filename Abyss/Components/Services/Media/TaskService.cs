using Abyss.Components.Services.Misc;
using Abyss.Components.Services.Security;
using Abyss.Components.Static;
using Abyss.Model.Media;
using Newtonsoft.Json;
using SQLite;
using Task = Abyss.Model.Media.Task;

namespace Abyss.Components.Services.Media;



public class TaskService(ConfigureService config, ResourceService rs, UserService user)
{ 
    public readonly string TaskFolder = Path.Combine(config.MediaRoot, "Tasks");
    public readonly string VideoFolder = Path.Combine(config.MediaRoot, "Videos");
    
    private const ulong MaxChunkSize = 20 * 1024 * 1024;
    
    public async Task<List<String>> Query(string token, string ip)
    {
        var r = await rs.Query(TaskFolder, token, ip);
        var u = user.Validate(token, ip);
        
        List<string> s = new();
        foreach (var i in r ?? [])
        {
            var p = Helpers.SafePathCombine(TaskFolder, [i, "task.json"]);
            var c = JsonConvert.DeserializeObject<Task>(await File.ReadAllTextAsync(p ?? ""));
            
            if(c?.Owner == u) s.Add(i);
        }
        
        return s;
    }
    
    public static uint GenerateUniqueId(string parentDirectory)
    {
        string[] directories = Directory.GetDirectories(parentDirectory);
        HashSet<uint> existingIds = new HashSet<uint>();

        foreach (string dirPath in directories)
        {
            string dirName = new DirectoryInfo(dirPath).Name;
            if (uint.TryParse(dirName, out uint id))
            {
                if (id != 0)
                {
                    existingIds.Add(id);
                }
            }
        }

        uint newId = 1;
        while (existingIds.Contains(newId))
        {
            newId++;
            if (newId == uint.MaxValue)
            {
                return 0;
            }
        }

        return newId;
    }
    
    public static bool IsFileNameSafe(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (fileName.Any(c => invalidChars.Contains(c)))
        {
            return false;
        }

        string[] reservedNames = {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        if (reservedNames.Contains(nameWithoutExtension))
        {
            return false;
        }

        return true;
    }
    
    public static void CreateEmptyFile(string filePath, long sizeInBytes)
    {
        using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(sizeInBytes);
        }
    }
    
    public static long GetAvailableFreeSpace(string directoryPath)
    {
        try
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                return -1;
            }

            string rootPath = Path.GetPathRoot(directoryPath) ?? "";
            
            if (string.IsNullOrEmpty(rootPath))
            {
                return -1;
            }

            DriveInfo driveInfo = new DriveInfo(rootPath);

            if (driveInfo.IsReady)
            {
                return driveInfo.AvailableFreeSpace;
            }
            else
            {
                return -1;
            }
        }
        catch (Exception)
        {
            return -1;
        }
    }
}