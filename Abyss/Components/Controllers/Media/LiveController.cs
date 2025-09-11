using Abyss.Components.Services;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Controllers.Media;

[ApiController]
[Route("api/[controller]")]
public class LiveController(ILogger<LiveController> logger, ResourceService rs, ConfigureService config): BaseController
{
    public readonly string LiveFolder = Path.Combine(config.MediaRoot, "Live");

    [HttpPost("{id}")]
    public async Task<IActionResult> AddLive(string id, string token, string owner)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        bool r = await rs.Include(d, token, Ip, owner, "rw,--,--");

        return r ? Ok("Success") : BadRequest();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveLive(string id, string token)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id]);
        if (d == null) 
            return StatusCode(403, new { message = "403 Denied" });

        bool r = await rs.Exclude(d, token, Ip);

        return r ? Ok("Success") : BadRequest();
    }

    [HttpGet("{id}/{token}/{item}")]
    public async Task<IActionResult> GetLive(string id, string token, string item)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id, item]);
        var f = Helpers.SafePathCombine(LiveFolder, [id]);
        if (d == null || f == null) return BadRequest();

        // TODO: (History)ffplay does not add the m3u8 query parameter in ts requests, so special treatment is given to ts here
        // TODO: (History)It should be pointed out that this implementation is not secure and should be modified in subsequent updates
        
        // TODO: It's still not very elegant, but it's a bit better to some extent

        bool r = await rs.Valid(f, token, OperationType.Read, Ip);
        if(!r) return StatusCode(403, new { message = "403 Denied" });
        
        if(System.IO.File.Exists(d))
            return PhysicalFile(d, Helpers.GetContentType(d));
        else 
            return NotFound();
    }
}