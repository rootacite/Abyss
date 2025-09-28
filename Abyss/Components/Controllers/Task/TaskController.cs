using Abyss.Components.Services;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Abyss.Model;
using Abyss.Model.Media;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Abyss.Components.Controllers.Task;


[ApiController]
[Route("api/[controller]")]
public class TaskController(ConfigureService config, TaskService taskService) : Controller
{
    public readonly string TaskFolder = Path.Combine(config.MediaRoot, "Tasks");
    
    [HttpGet]
    public async Task<IActionResult> Query(string token)
    {
        // If the token is invalid, an empty list will be returned, which is part of the design
        return Json(await taskService.Query(token, Ip));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string token, [FromBody] TaskCreation creation)
    {
        var r = await taskService.Create(token, Ip, creation);
        if(r == null)
        { 
            return BadRequest();
        }
        return Ok(JsonConvert.SerializeObject(r, Formatting.Indented));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTask(string id)
    {
        throw new NotImplementedException();
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> PutChip(string id)
    {
        throw new NotImplementedException();
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> VerifyChip(string id)
    {
        throw new NotImplementedException();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTask(string id)
    {
        throw new NotImplementedException();
    }
    
    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
}