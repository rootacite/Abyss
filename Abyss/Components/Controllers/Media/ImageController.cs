using Abyss.Components.Services.Media;
using Abyss.Components.Static;
using Abyss.Components.Tools;
using Abyss.Model.Media;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Abyss.Components.Controllers.Media;

[ApiController]
[Route("api/[controller]")]
public class ImageController(ComicService comicService) : BaseController
{
    
    [HttpPost("init")]
    public async Task<IActionResult> InitAsync(string token, string owner)
    {
        var r = await comicService.InitAsync(token, owner, Ip);
        return r ? Ok("Initialize Success") : _403;
    }

    [HttpGet]
    public async Task<IActionResult> QueryCollections(string token)
    {
        var r = await comicService.QueryCollections(token, Ip);
        return r != null ? Ok(r.NaturalSort(x => x)) : _403;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Query(string id, string token)
    {
        var r =  await comicService.Query(id, token, Ip);
        return r != null ? Ok(r) : _403;
    }
    
    [HttpPost("bulkquery")]
    public async Task<IActionResult> QueryBulk([FromQuery] string token, [FromBody] string[] id)
    {
        var r = await comicService.QueryBulk(token, id, Ip);
        return Ok(JsonConvert.SerializeObject(r));
    }

    [HttpPost("{id}/bookmark")]
    public async Task<IActionResult> Bookmark(string id, string token, [FromBody] Bookmark bookmark)
    {
        var r = await comicService.Bookmark(id, token, bookmark, Ip);
        return r ? Ok("Success") : _403;
    }
    
    [HttpGet("{id}/{file}")]
    public async Task<IActionResult> Get(string id, string file, string token)
    {
        var r = await comicService.Page(id, file, token, Ip);
        return r ?? _403;
    }
}