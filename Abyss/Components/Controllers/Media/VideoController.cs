using System.Diagnostics;
using Abyss.Components.Services;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Controllers.Media;

[ApiController]
[Route("api/[controller]")]
public class VideoController(ILogger<VideoController> logger, ResourceService rs, ConfigureService config) : Controller
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
        var r = await rs.Query(VideoFolder, token, Ip);
        
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
        
        return Ok(r);
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
    
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
}