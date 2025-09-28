
using Abyss.Components.Services.Media;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Abyss.Components.Controllers.Media;

[ApiController]
[Route("api/[controller]")]
public class VideoController(VideoService videoService)
    : BaseController
{
    
    [HttpPost("init")]
    public async Task<IActionResult> InitAsync(string token, string owner)
    {
        if (await videoService.Init(token, owner, Ip))
            return Ok("Initialized Successfully");
        return _403;
    }

    [HttpGet]
    public async Task<IActionResult> GetClass(string token)
    {
        var r = await videoService.GetClasses(token, Ip);
        return r != null ? Ok(r) : _403; 
    }

    [HttpGet("{klass}")]
    public async Task<IActionResult> QueryClass(string klass, string token)
    {
        var r = await videoService.QueryClass(klass, token, Ip);
        return r != null ? Ok(r) : _403; 
    }

    [HttpGet("{klass}/{id}")]
    public async Task<IActionResult> QueryVideo(string klass, string id, string token)
    {
        var r = await videoService.QueryVideo(klass, id, token, Ip);
        return r != null ? Ok(r) : _403;
    }

    [HttpPost("{klass}/bulkquery")]
    public async Task<IActionResult> QueryBulk([FromQuery] string token, [FromBody] string[] id,
        [FromRoute] string klass)
    {
        var r = await videoService.QueryBulk(klass, id, token, Ip);
        return Ok(JsonConvert.SerializeObject(r));
    }

    [HttpGet("{klass}/{id}/cover")]
    public async Task<IActionResult> Cover(string klass, string id, string token)
    {
        var r = await videoService.Cover(klass, id, token, Ip);
        return r ?? _403;
    }

    [HttpGet("{klass}/{id}/gallery/{pic}")]
    public async Task<IActionResult> Gallery(string klass, string id, string pic, string token)
    {
        var r = await videoService.Gallery(klass, id, pic, token, Ip);
        return r ?? _403;
    }

    [HttpGet("{klass}/{id}/subtitle")]
    public async Task<IActionResult> Subtitle(string klass, string id, string token)
    {
        var r = await videoService.Subtitle(klass, id, token, Ip);
        return r ?? _404;
    }

    [HttpGet("{klass}/{id}/av")]
    public async Task<IActionResult> Av(string klass, string id, string token)
    {
        var r = await videoService.Av(klass, id, token, Ip);
        return r ?? _403;
    }
}