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
    public async Task<IActionResult> InitAsync(string owner)
    {
        var r = await comicService.InitAsync(Token, owner, Ip);
        return r ? Ok("Initialize Success") : _403;
    }

    [HttpGet]
    public async Task<IActionResult> QueryCollections()
    {
        var r = await comicService.QueryCollections(Token, Ip);
        return r != null ? Ok(r.NaturalSort(x => x)) : _403;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Query(string id)
    {
        var r =  await comicService.Query(id, Token, Ip);
        return r != null ? Ok(r) : _403;
    }
    
    [HttpPost("bulkquery")]
    public async Task<IActionResult> QueryBulk([FromBody] string[] id)
    {
        var r = await comicService.QueryBulk(Token, id, Ip);
        return Ok(JsonConvert.SerializeObject(r));
    }

    [HttpPost("{id}/bookmark")]
    public async Task<IActionResult> Bookmark(string id, [FromBody] Bookmark bookmark)
    {
        var r = await comicService.Bookmark(id, Token, bookmark, Ip);
        return r ? Ok("Success") : _403;
    }
    
    [HttpGet("{id}/{file}")]
    public async Task<IActionResult> Get(string id, string file)
    {
        var r = await comicService.Page(id, file, Token, Ip);
        return r ?? _403;
    }
    
    [HttpGet("{id}/achieve")]
    public async Task<IActionResult> Achieve(string id)
    {
        var r = await comicService.Achieve(id, Token, Ip);
        return r ?? _404;
    }
}