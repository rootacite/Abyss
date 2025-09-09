using Abyss.Components.Services;
using Abyss.Components.Static;
using Abyss.Components.Tools;
using Abyss.Model;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Abyss.Components.Controllers.Media;
using System.IO;

[ApiController]
[Route("api/[controller]")]
public class ImageController(ILogger<ImageController> logger, ResourceService rs, ConfigureService config) : Controller
{
    public readonly string ImageFolder = Path.Combine(config.MediaRoot, "Images");
    
    [HttpPost("init")]
    public async Task<IActionResult> InitAsync(string token, string owner)
    {
        var r = await rs.Initialize(ImageFolder, token, owner, Ip);
        if(r) return Ok(r);
        return StatusCode(403, new { message = "403 Denied" });
    }

    [HttpGet]
    public async Task<IActionResult> QueryCollections(string token)
    {
        var r = await rs.Query(ImageFolder, token, Ip);
        
        if(r == null) 
            return StatusCode(401, new { message = "Unauthorized" });
        
        return Ok(r.NaturalSort(x => x));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Query(string id, string token)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, "summary.json"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Get(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        
        return Ok(await System.IO.File.ReadAllTextAsync(d));
    }

    [HttpPost("{id}/bookmark")]
    public async Task<IActionResult> Bookmark(string id, string token, [FromBody] Bookmark bookmark)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, "summary.json"]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });

        var r = await rs.Update(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });

        Comic c = JsonConvert.DeserializeObject<Comic>(await System.IO.File.ReadAllTextAsync(d))!;
        
        var bookmarkPage = Helpers.SafePathCombine(ImageFolder, [id, bookmark.Page]);
        if(!System.IO.File.Exists(bookmarkPage))
            return BadRequest();
        
        c.Bookmarks.Add(bookmark);
        var o = JsonConvert.SerializeObject(c);
        await System.IO.File.WriteAllTextAsync(d, o);
        return Ok();
    }
    
    [HttpGet("{id}/{file}")]
    public async Task<IActionResult> Get(string id, string file, string token)
    {
        var d = Helpers.SafePathCombine(ImageFolder, [id, file]);
        if (d == null) return StatusCode(403, new { message = "403 Denied" });
        
        var r = await rs.Get(d, token, Ip);
        if (!r)  return StatusCode(403, new { message = "403 Denied" });
        
        return PhysicalFile(d, "image/jpeg", enableRangeProcessing: true);
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
}