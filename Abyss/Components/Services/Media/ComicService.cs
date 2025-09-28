using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Abyss.Model.Media;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Task = System.Threading.Tasks.Task;

namespace Abyss.Components.Services.Media;

public class ComicService(ILogger<ComicService> logger, ResourceService rs, ConfigureService config)
{
    public readonly string ImageFolder = Path.Combine(config.MediaRoot, "Images");

    public async Task<bool> InitAsync(string token, string owner, string ip)
        => await rs.Initialize(ImageFolder, token, owner, ip);

    public async Task<string[]?> QueryCollections(string token, string ip)
        => await rs.Query(ImageFolder, token, ip);

    public async Task<string?> Query(string id, string token, string ip)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, "summary.json"]);
        if(d != null)
            return await rs.GetString(d, token, ip);
        return null;
    }

    public async Task<Comic?[]> QueryBulk(string token, string[] id, string ip)
    {
        var db = id.Select(x => Helpers.SafePathCombine(ImageFolder, [x, "summary.json"])).ToArray();
        if (db.Any(x => x == null))
            return [];
        
        var sm = await rs.GetAllString(db!, token, ip);
        return sm.Select(x => x.Value == null ? null : JsonConvert.DeserializeObject<Comic>(x.Value)).ToArray();
    }

    public async Task<bool> Bookmark(string id, string token, Bookmark bookmark, string ip)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, "summary.json"]);
        if (d == null) 
            return false;
        
        Comic c = JsonConvert.DeserializeObject<Comic>(await File.ReadAllTextAsync(d))!;
        
        var bookmarkPage = Helpers.SafePathCombine(ImageFolder, [id, bookmark.Page]);
        if (File.Exists(bookmarkPage))
        {
            c.Bookmarks.Add(bookmark);
            var o = JsonConvert.SerializeObject(c);
            return await rs.UpdateString(d, token, ip, o);
        }
        
        return false;
    }

    public async Task<PhysicalFileResult?> Page(string id, string file, string token, string ip)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, file]);
        if (d != null)
        {
            return await rs.Get(d, token, ip, "image/jpeg");
        }

        return null;
    }
}