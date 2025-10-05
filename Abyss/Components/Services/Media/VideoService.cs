using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Abyss.Model.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Newtonsoft.Json;

namespace Abyss.Components.Services.Media;

public class VideoService(ResourceService rs, ConfigureService config)
{
    public readonly string VideoFolder = Path.Combine(config.MediaRoot, "Videos");

    public async Task<bool> Init(string token, string owner, string ip)
        => await rs.Initialize(VideoFolder, token, owner, ip);
    
    public async Task<string[]?> GetClasses(string token, string ip) 
        => (await rs.Query(VideoFolder, token, ip))?.SortLikeWindows();

    public async Task<string[]?> QueryClass(string klass, string token, string ip)
    {
        var d = Helpers.SafePathCombine(VideoFolder, klass);
        if (d != null)
        {
            return await rs.Query(d, token, ip);
        }

        return null;
    }

    public async Task<string?> QueryVideo(string klass, string id, string token, string ip)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "summary.json"]);
        if(d != null)
            return await rs.GetString(d, token, ip);
        return null;
    }

    public async Task<Video?[]> QueryBulk(string klass, string[] id, string token, string ip)
    {
        var db = id.Select(x => Helpers.SafePathCombine(VideoFolder, [klass, x, "summary.json"])).ToArray();
        if (db.Any(x => x == null))
            return [];

        var sm = await rs.GetAllString(db!, token, ip);
        return sm.Select(x => x.Value == null ? null : JsonConvert.DeserializeObject<Video>(x.Value)).ToArray();
    }

    public async Task<PhysicalFileResult?> Cover(string klass, string id, string token, string ip)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "cover.jpg"]);
        if (d != null)
        {
            return await rs.Get(d, token, ip, "image/jpeg");
        }

        return null;
    }

    public async Task<PhysicalFileResult?> Gallery(string klass, string id, string pic, string token, string ip)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "gallery", pic]);
        if (d != null)
        {
            return await rs.Get(d, token, ip, "image/jpeg");
        }
        
        return null;
    }

    public async Task<PhysicalFileResult?> Subtitle(string klass, string id, string token, string ip)
    {
        var folder = Helpers.SafePathCombine(VideoFolder, new[] { klass, id });
        if (folder == null)
            return null;

        string? subtitlePath;

        try
        {
            var preferredVtt = Path.Combine(folder, "subtitle.vtt");
            if (File.Exists(preferredVtt))
            {
                subtitlePath = preferredVtt;
            }
            else
            {
                subtitlePath = Directory.EnumerateFiles(folder, "*.vtt").FirstOrDefault();

                if (subtitlePath == null)
                {
                    var preferredAss = Path.Combine(folder, "subtitle.ass");
                    if (File.Exists(preferredAss))
                    {
                        subtitlePath = preferredAss;
                    }
                    else
                    {
                        subtitlePath = Directory.EnumerateFiles(folder, "*.ass").FirstOrDefault();
                    }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        if (subtitlePath == null)
            return null;

        var ext = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".vtt" => "text/vtt",
            ".ass" => "text/x-ssa",
            _ => "text/plain"
        };

        return await rs.Get(subtitlePath, token, ip, contentType);
    }

    public async Task<PhysicalFileResult?> Av(string klass, string id, string token, string ip)
    {
        var folder = Helpers.SafePathCombine(VideoFolder, new[] { klass, id });
        if (folder == null)
            return null;
        
        var allowedExt = new[] { ".mp4", ".mkv", ".webm", ".mov", ".ogg" };
        
        string? videoPath = null;
        
        foreach (var ext in allowedExt)
        {
            var p = Path.Combine(folder, "video" + ext);
            if (File.Exists(p))
            {
                videoPath = p;
                break;
            }
        }

        if (videoPath == null)
            return null;
        
        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(videoPath, out var contentType))
        {
            var ext = Path.GetExtension(videoPath).ToLowerInvariant();
            contentType = ext switch
            {
                ".mkv" => "video/x-matroska",
                ".mp4" => "video/mp4",
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".ogg" => "video/ogg",
                _ => "application/octet-stream",
            };
        }
        
        return await rs.Get(videoPath, token, ip, contentType);
    }
}