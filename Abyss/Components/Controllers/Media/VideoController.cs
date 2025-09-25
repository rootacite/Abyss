using Abyss.Components.Services;
using Abyss.Components.Static;
using Abyss.Components.Tools;
using Abyss.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Newtonsoft.Json;

namespace Abyss.Components.Controllers.Media;

using Task = System.Threading.Tasks.Task;

[ApiController]
[Route("api/[controller]")]
public class VideoController(ILogger<VideoController> logger, ResourceService rs, ConfigureService config)
    : BaseController
{
    private ILogger<VideoController> _logger = logger;
    public readonly string VideoFolder = Path.Combine(config.MediaRoot, "Videos");

    [HttpPost("init")]
    public async Task<IActionResult> InitAsync(string token, string owner)
    {
        var r = await rs.Initialize(VideoFolder, token, owner, Ip);
        if (r) return Ok(r);
        return StatusCode(403, new { message = "403 Denied" });
    }

    [HttpGet]
    public async Task<IActionResult> GetClass(string token)
    {
        var r = (await rs.Query(VideoFolder, token, Ip))?.SortLikeWindows();

        if (r == null)
            return StatusCode(401, new { message = "Unauthorized" });

        return Ok(r);
    }

    [HttpGet("{klass}")]
    public async Task<IActionResult> QueryClass(string klass, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, klass);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });
        
        var r = await rs.Query(d, token, Ip);
        if (r == null) return StatusCode(401, new { message = "Unauthorized" });

        var rv = r.Select(x => { return Helpers.SafePathCombine(VideoFolder, [klass, x, "summary.json"]); }).ToArray();

        for (int i = 0; i < rv.Length; i++)
        {
            if (rv[i] == null) continue;
            rv[i] = await System.IO.File.ReadAllTextAsync(rv[i] ?? "");
        }

        var sv = rv.Where(x => x != null).Select(x => x ?? "")
            .Select(x => JsonConvert.DeserializeObject<Video>(x)).ToArray();


        return Ok(sv.Zip(r, (x, y) => (x, y)).NaturalSort(x => x.x!.name).Select(x => x.y).ToArray());
    }

    [HttpGet("{klass}/{id}")]
    public async Task<IActionResult> QueryVideo(string klass, string id, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "summary.json"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Get(d, token, Ip);
        if (!r) return StatusCode(403, new { message = "403 Denied" });

        return Ok(await System.IO.File.ReadAllTextAsync(d));
    }

    [HttpPost("{klass}/bulkquery")]
    public async Task<IActionResult> QueryBulk([FromQuery] string token, [FromBody] string[] id,
        [FromRoute] string klass)
    {
        var db = id.Select(x => Helpers.SafePathCombine(VideoFolder, [klass, x, "summary.json"])).ToArray();
        if (db.Any(x => x == null))
            return BadRequest();

        if (!await rs.GetAll(db!, token, Ip))
            return StatusCode(403, new { message = "403 Denied" });

        var rc = db.Select(x => System.IO.File.ReadAllTextAsync(x!)).ToArray();
        string[] rcs = await Task.WhenAll(rc);
        var rjs = rcs.Select(JsonConvert.DeserializeObject<Video>).Select(x => x!).ToList();

        return Ok(JsonConvert.SerializeObject(rjs));
    }

    [HttpGet("{klass}/{id}/cover")]
    public async Task<IActionResult> Cover(string klass, string id, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "cover.jpg"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Get(d, token, Ip);
        if (!r) return StatusCode(403, new { message = "403 Denied" });

        return PhysicalFile(d, "image/jpeg", enableRangeProcessing: true);
    }

    [HttpGet("{klass}/{id}/gallery/{pic}")]
    public async Task<IActionResult> Gallery(string klass, string id, string pic, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "gallery", pic]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Get(d, token, Ip);
        if (!r) return StatusCode(403, new { message = "403 Denied" });

        return PhysicalFile(d, "image/jpeg", enableRangeProcessing: true);
    }

    [HttpGet("{klass}/{id}/subtitle")]
    public async Task<IActionResult> Subtitle(string klass, string id, string token)
    {
        var folder = Helpers.SafePathCombine(VideoFolder, new[] { klass, id });
        if (folder == null)
            return StatusCode(403, new { message = "403 Denied" });

        string? subtitlePath = null;

        try
        {
            var preferredVtt = Path.Combine(folder, "subtitle.vtt");
            if (System.IO.File.Exists(preferredVtt))
            {
                subtitlePath = preferredVtt;
            }
            else
            {
                subtitlePath = Directory.EnumerateFiles(folder, "*.vtt").FirstOrDefault();

                if (subtitlePath == null)
                {
                    var preferredAss = Path.Combine(folder, "subtitle.ass");
                    if (System.IO.File.Exists(preferredAss))
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
            return NotFound(new { message = "video folder not found" });
        }

        if (subtitlePath == null)
            return NotFound(new { message = "subtitle not found" });

        var r = await rs.Get(subtitlePath, token, Ip);
        if (!r)
            return StatusCode(403, new { message = "403 Denied" });

        var ext = Path.GetExtension(subtitlePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".vtt" => "text/vtt",
            ".ass" => "text/x-ssa",
            _ => "text/plain"
        };

        return PhysicalFile(subtitlePath, contentType, enableRangeProcessing: false);
    }

    [HttpGet("{klass}/{id}/av")]
    public async Task<IActionResult> Av(string klass, string id, string token)
    {
        var folder = Helpers.SafePathCombine(VideoFolder, new[] { klass, id });
        if (folder == null) return StatusCode(403, new { message = "403 Denied" });
        
        var allowedExt = new[] { ".mp4", ".mkv", ".webm", ".mov", ".ogg" };
        
        string? videoPath = null;
        
        foreach (var ext in allowedExt)
        {
            var p = Path.Combine(folder, "video" + ext);
            if (System.IO.File.Exists(p))
            {
                videoPath = p;
                break;
            }
        }
        
        if (videoPath == null)
        {
            try
            {
                videoPath = Directory.EnumerateFiles(folder)
                    .FirstOrDefault(f => allowedExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
            }
            catch (DirectoryNotFoundException)
            {
                return NotFound(new { message = "video folder not found" });
            }
        }

        if (videoPath == null) return NotFound(new { message = "video not found" });
        
        var r = await rs.Get(videoPath, token, Ip);
        if (!r) return StatusCode(403, new { message = "403 Denied" });
        
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
        
        return PhysicalFile(videoPath, contentType, enableRangeProcessing: true);
    }
}