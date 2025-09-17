using System.Diagnostics;
using Abyss.Components.Services;
using Abyss.Components.Static;
using Abyss.Components.Tools;
using Abyss.Model;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Abyss.Components.Controllers.Media;

using Task = System.Threading.Tasks.Task;

[ApiController]
[Route("api/[controller]")]
public class VideoController(ILogger<VideoController> logger, ResourceService rs, ConfigureService config) : BaseController
{
    private ILogger<VideoController> _logger = logger;

    public readonly string VideoFolder = Path.Combine(config.MediaRoot, "Videos");

    [HttpPost("init")]
    public async Task<IActionResult> InitAsync(string token, string owner)
    {
        var r = await rs.Initialize(VideoFolder, token, owner, Ip);
        if(r) return Ok(r);
        return StatusCode(403, new { message = "403 Denied" });
    }

    [HttpGet]
    public async Task<IActionResult> GetClass(string token)
    {
        var r = (await rs.Query(VideoFolder, token, Ip))?.SortLikeWindows();
        
        if(r == null) 
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

        var rv = r.Select(x =>
        {
            return Helpers.SafePathCombine(VideoFolder, [klass, x, "summary.json"]);
        }).ToArray();

        for (int i = 0; i < rv.Length; i++)
        {
            if(rv[i] == null) continue;
            rv[i] = await System.IO.File.ReadAllTextAsync(rv[i] ?? "");
        }

        var sv = rv.Where(x => x!=null).Select(x => x ?? "")
            .Select(x => JsonConvert.DeserializeObject<Video>(x)).ToArray();

        
        return Ok(sv.Zip(r, (x, y) => (x, y)).NaturalSort(x => x.x!.name).Select(x => x.y).ToArray());
    }
    
    [HttpGet("{klass}/{id}")]
    public async Task<IActionResult> QueryVideo(string klass, string id, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "summary.json"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Get(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        
        return Ok(await System.IO.File.ReadAllTextAsync(d));
    }

    [HttpPost("{klass}/bulkquery")]
    public async Task<IActionResult> QueryBulk([FromQuery] string token, [FromBody] string[] id, [FromRoute] string klass)
    {
        List<string> result = new List<string>();
        
        var db = id.Select(x => Helpers.SafePathCombine(VideoFolder, [klass, x, "summary.json"])).ToArray();
        if(db.Any(x => x == null))
            return BadRequest();
        
        if(!await rs.GetAll(db!, token, Ip))
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
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        
        return PhysicalFile(d, "image/jpeg", enableRangeProcessing: true);
    }

    [HttpGet("{klass}/{id}/gallery/{pic}")]
    public async Task<IActionResult> Gallery(string klass, string id, string pic, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "gallery", pic]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });
        
        var r = await rs.Get(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        
        return PhysicalFile(d, "image/jpeg", enableRangeProcessing: true);
    }

    [HttpGet("{klass}/{id}/av")]
    public async Task<IActionResult> Av(string klass, string id, string token)
    {
        var d = Helpers.SafePathCombine(VideoFolder, [klass, id, "video.mp4"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });
        
        var r = await rs.Get(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        return PhysicalFile(d, "video/mp4", enableRangeProcessing: true);
    }
}