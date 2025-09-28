using Abyss.Components.Services;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Controllers.Media;

[ApiController]
[Route("api/[controller]")]
public class LiveController(ResourceService rs, ConfigureService config): BaseController
{
    public readonly string LiveFolder = Path.Combine(config.MediaRoot, "Live");

    [HttpPost("{id}")]
    public async Task<IActionResult> AddLive(string id, string token, int owner)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id]);
        if (d == null) return _403;

        bool r = await rs.Include(d, token, Ip, owner, "rw,--,--");

        return r ? Ok("Success") : _400;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveLive(string id, string token)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id]);
        if (d == null) 
            return _403;

        bool r = await rs.Exclude(d, token, Ip);

        return r ? Ok("Success") : _400;
    }

    [HttpGet("{id}/{token}/{item}")]
    public async Task<IActionResult> GetLive(string id, string token, string item)
    {
        var d = Helpers.SafePathCombine(LiveFolder, [id, item]);
        if (d == null) return _400;

        // TODO: (History)ffplay does not add the m3u8 query parameter in ts requests, so special treatment is given to ts here
        // TODO: (History)It should be pointed out that this implementation is not secure and should be modified in subsequent updates
        
        // TODO: It's still not very elegant, but it's a bit better to some extent

        var r = await rs.Get(d, token, Ip, Helpers.GetContentType(d));
        return r ?? _404;
    }
}