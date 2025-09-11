
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
public class UserController(UserService user, ILogger<UserController> logger) : BaseController
{
    private readonly ILogger<UserController> _logger = logger;
    private readonly UserService _user = user;

    [HttpGet("{user}")]
    public async Task<IActionResult> Challenge(string user)
    {
        var c = await _user.Challenge(user);
        if(c == null) 
            return StatusCode(403, new { message = "Access forbidden" });
        
        return Ok(c);
    }

    [HttpPost("{user}")]
    public async Task<IActionResult> Challenge(string user, [FromBody] ChallengeResponse response)
    {
        var r = await _user.Verify(user, response.Response, Ip);
        if(r == null)
            return StatusCode(403, new { message = "Access forbidden" });
        
        return Ok(r);
    }

    [HttpPost("validate")]
    public IActionResult Validate(string token)
    {
        var u = _user.Validate(token, Ip);
        if (u == null)
        {
            return StatusCode(401, new { message = "Invalid" });
        }
        
        return Ok(u);
    }

    [HttpPost("destroy")]
    public IActionResult Destroy(string token)
    {
        var u = _user.Validate(token, Ip);
        if (u == null)
        {
            return StatusCode(401, new { message = "Invalid" });
        }
        
        _user.Destroy(token);
        return Ok("Success");
    }

    [HttpPatch("{user}")]
    public async Task<IActionResult> Create(string user, [FromBody] UserCreating creating)
    {
        // Valid token
        var r = await _user.Verify(user, creating.Response, Ip);
        if(r == null)
            return StatusCode(403, new { message = "Denied" });
        
        // User exists ?
        var cu = await _user.QueryUser(creating.Name);
        if(cu != null) 
            return StatusCode(403, new { message = "Denied" });
        
        // Valid username string
        if(!IsAlphanumeric(creating.Name))
            return StatusCode(403, new { message = "Denied" });
        
        // Valid parent && Privilege
        var ou = await _user.QueryUser(_user.Validate(r, Ip) ?? "");
        if(creating.Parent != (_user.Validate(r, Ip) ?? "") || creating.Privilege > ou?.Privilege)
            return StatusCode(403, new { message = "Denied" });
            
        await _user.CreateUser(new User()
        {
            Name = creating.Name,
            Parent = _user.Validate(r, Ip) ?? "",
            Privilege = creating.Privilege,
            PublicKey = creating.PublicKey,
        } );
        
        _user.Destroy(r);
        return Ok("Success");
    }
    
    [HttpGet("{user}/open")]
    public async Task<IActionResult> Open(string user, [FromQuery] string token, [FromQuery] string? bindIp = null)
    {
        var caller = _user.Validate(token, Ip);
        if (caller == null || caller != "root")
        {
            return StatusCode(403, new { message = "Access forbidden" });
        }

        var target = await _user.QueryUser(user);
        if (target == null)
        {
            return StatusCode(404, new { message = "User not found" });
        }

        var ipToBind = string.IsNullOrWhiteSpace(bindIp) ? Ip : bindIp;

        var t = _user.CreateToken(user, ipToBind, TimeSpan.FromHours(1));

        _logger.LogInformation("Root created 1h token for {User}, bound to {BindIp}, request from {ReqIp}", user, ipToBind, Ip);
        return Ok(new { token = t, user, boundIp = ipToBind });
    }
    
    public static bool IsAlphanumeric(string input)
    {
        if (string.IsNullOrEmpty(input))
            return false;
        return Regex.IsMatch(input, @"^[a-zA-Z0-9]+$");
    }
}