// UserController.cs

using System.Text.RegularExpressions;
using Abyss.Components.Services;
using Abyss.Components.Static;
using Abyss.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Abyss.Components.Controllers.Security;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("Fixed")]
public class UserController(UserService userService, ILogger<UserController> logger) : BaseController
{
    private readonly ILogger<UserController> _logger = logger;
    private readonly UserService _userService = userService;

    [HttpGet("{user}")]
    public async Task<IActionResult> Challenge(string user)
    {
        var c = await _userService.Challenge(user);
        if (c == null)
            return StatusCode(403, new { message = "Access forbidden" });

        return Ok(c);
    }

    [HttpPost("{user}")]
    public async Task<IActionResult> Challenge(string user, [FromBody] ChallengeResponse response)
    {
        var r = await _userService.Verify(user, response.Response, Ip);
        if (r == null)
            return StatusCode(403, new { message = "Access forbidden" });
        return Ok(r);
    }

    [HttpPost("validate")]
    public IActionResult Validate(string token)
    {
        var u = _userService.Validate(token, Ip);
        if (u == -1)
        {
            return StatusCode(401, new { message = "Invalid" });
        }

        return Ok(u);
    }

    [HttpPost("destroy")]
    public IActionResult Destroy(string token)
    {
        var u = _userService.Validate(token, Ip);
        if (u == -1)
        {
            return StatusCode(401, new { message = "Invalid" });
        }

        _userService.Destroy(token);
        return Ok("Success");
    }

    [HttpPatch("{user}")]
    public async Task<IActionResult> Create(string user, [FromBody] UserCreating creating)
    {
        // Valid token
        var r = await _userService.Verify(user, creating.Response, Ip);
        if (r == null)
            return StatusCode(403, new { message = "Denied" });

        // User exists ?
        var cu = await _userService.QueryUser(creating.Name);
        if (cu != null)
            return StatusCode(403, new { message = "Denied" });

        // Valid username string
        if (!IsAlphanumeric(creating.Name))
            return StatusCode(403, new { message = "Denied" });

        // Valid parent && Privilege
        var ou = await _userService.QueryUser(_userService.Validate(r, Ip));
        if (creating.Privilege > ou?.Privilege || ou == null)
            return StatusCode(403, new { message = "Denied" });

        await _userService.CreateUser(new User
        {
            Username = creating.Name,
            ParentId = ou.Uuid,
            Privilege = creating.Privilege,
            PublicKey = creating.PublicKey,
        });

        _userService.Destroy(r);
        return Ok("Success");
    }

    [HttpGet("{user}/open")]
    public async Task<IActionResult> Open(string user, [FromQuery] string token, [FromQuery] string? bindIp = null)
    {
        var caller = _userService.Validate(token, Ip);
        if (caller != 1)
        {
            return StatusCode(403, new { message = "Access forbidden" });
        }

        var target = await _userService.QueryUser(user);
        if (target == null)
        {
            return StatusCode(404, new { message = "User not found" });
        }

        var ipToBind = string.IsNullOrWhiteSpace(bindIp) ? Ip : bindIp;

        var t = _userService.CreateToken(target.Uuid, ipToBind, TimeSpan.FromHours(1));

        _logger.LogInformation("Root created 1h token for {User}, bound to {BindIp}, request from {ReqIp}", user,
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