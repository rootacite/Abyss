// UserController.cs

using System.Text.RegularExpressions;
using Abyss.Components.Services;
using Abyss.Components.Services.Security;
using Abyss.Components.Static;
using Abyss.Model;
using Abyss.Model.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Abyss.Components.Controllers.Security;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("Fixed")]
public class UserController(UserService userService, ILogger<UserController> logger) : BaseController
{
    [HttpGet("{user}")]
    public async Task<IActionResult> Challenge(string user)
    {
        var c = await userService.Challenge(user);
        return c != null ? Ok(c): _403;
    }

    [HttpPost("{user}")]
    public async Task<IActionResult> Challenge(string user, [FromBody] ChallengeResponse response)
    {
        var r = await userService.Verify(user, response.Response, Ip);
        if (r != null)
        {
            Response.Cookies.Append("token", r);
            return Ok(r);
        }
        
        return _403;
    }

    [HttpPost("validate")]
    public IActionResult Validate(string token)
    {
        var u = userService.Validate(token, Ip);
        return u == -1 ? _401 : Ok(u);
    }

    [HttpPost("destroy")]
    public IActionResult Destroy(string token)
    {
        var u = userService.Validate(token, Ip);
        if (u != -1)
        {
            userService.Destroy(token);
            return Ok("Success");
        }
        return _401;
    }

    [HttpPatch("{user}")]
    public async Task<IActionResult> Create(string user, [FromBody] UserCreating creating)
    {
        bool r = await userService.CreateUserAsync(user, creating, Ip);
        return r ? Ok("Success") : _403;
    }

    [HttpGet("{user}/open")]
    public async Task<IActionResult> Open(string user, [FromQuery] string token, [FromQuery] string? bindIp = null)
    {
        string? r = await userService.OpenUserAsync(user, token, bindIp, Ip);
        return r != null ? Ok(r) : _403;
    }
}