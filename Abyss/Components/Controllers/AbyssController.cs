using Abyss.Components.Services;
using Abyss.Components.Services.Misc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Abyss.Components.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AbyssController(ILogger<AbyssController> logger, ConfigureService config) : Controller
{
    private ILogger<AbyssController> _logger = logger;
    private ConfigureService _config = config;

    [HttpGet]
    public IActionResult GetCollection()
    {
        return Ok($"Abyss {_config.Version}. \nMediaRoot: {_config.MediaRoot}");
    }
}