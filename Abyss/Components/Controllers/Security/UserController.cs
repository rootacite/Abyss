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
        if (c == null)
            return _403;

        return Ok(c);
    }

    [HttpPost("{user}")]
    public async Task<IActionResult> Challenge(string user, [FromBody] ChallengeResponse response)
    {
        var r = await userService.Verify(user, response.Response, Ip);
        if (r == null)
            return _403;
        
        Response.Cookies.Append("token", r);
        return Ok(r);
    }

    [HttpPost("validate")]
    public IActionResult Validate(string token)
    {
        var u = userService.Validate(token, Ip);
        if (u == -1)
        {
            return _401;
        }

        return Ok(u);
    }

    [HttpPost("destroy")]
    public IActionResult Destroy(string token)
    {
        var u = userService.Validate(token, Ip);
        if (u == -1)
        {
            return _401;
        }

        userService.Destroy(token);
        return Ok("Success");
    }

    [HttpPatch("{user}")]
    public async Task<IActionResult> Create(string user, [FromBody] UserCreating creating)
    {
        // Valid token
        var r = await userService.Verify(user, creating.Response, Ip);
        if (r == null)
            return _403;

        // User exists ?
        var cu = await userService.QueryUser(creating.Name);
        if (cu != null)
            return _403;

        // Valid username string
        if (!IsAlphanumeric(creating.Name))
            return _403;

        // Valid parent && Privilege
        var ou = await userService.QueryUser(userService.Validate(r, Ip));
        if (creating.Privilege > ou?.Privilege || ou == null)
            return _403;

        await userService.CreateUser(new User
        {
            Username = creating.Name,
            ParentId = ou.Uuid,
            Privilege = creating.Privilege,
            PublicKey = creating.PublicKey,
        });

        userService.Destroy(r);
        return Ok("Success");
    }

    [HttpGet("{user}/open")]
    public async Task<IActionResult> Open(string user, [FromQuery] string token, [FromQuery] string? bindIp = null)
    {
        var caller = userService.Validate(token, Ip);
        if (caller != 1)
        {
            return _403;
        }

        var target = await userService.QueryUser(user);
        if (target == null)
        {
            return _403;
        }

        var ipToBind = string.IsNullOrWhiteSpace(bindIp) ? Ip : bindIp;

        var t = userService.CreateToken(target.Uuid, ipToBind, TimeSpan.FromHours(1));

        logger.LogInformation("Root created 1h token for {User}, bound to {BindIp}, request from {ReqIp}", user,
            ipToBind, Ip);
        return Ok(new { token = t, user, boundIp = ipToBind });
    }

    public static bool IsAlphanumeric(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;
        return Regex.IsMatch(input, @"^[a-zA-Z0-9]+$");
    }
}