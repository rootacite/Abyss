using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Static;

public abstract class BaseController : Controller
{
    protected IActionResult _403 => StatusCode(403, new { message = "Access Denied" });
    protected IActionResult _400 => StatusCode(400, new { message = "Bad Request" });
    protected IActionResult _401 => StatusCode(404, new { message = "Unauthorized" });
    protected IActionResult _404 => StatusCode(404, new { message = "Not Found" });

    protected string Token
    {
        get
        {
            var t = Request.Cookies["token"];
            if (string.IsNullOrEmpty(t))
                throw new AuthenticationException("Token is missing");
            
            return t;
        }
    }
    
    private string? _ip;

    protected string Ip
    {
        get
        {
            if (_ip != null)
                return _ip;

            _ip = GetClientIpAddress();
                
            if (string.IsNullOrEmpty(_ip))
                throw new InvalidOperationException("invalid IP");

            return _ip;
        }
    }

    private string? GetClientIpAddress()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
            
        if (remoteIp != null && (IPAddress.IsLoopback(remoteIp) || remoteIp.ToString() == "::1"))
        {
            return remoteIp.ToString();
        }

        string? ip = remoteIp?.ToString();

        if (HttpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var forwardedIps = forwardedFor.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();

            if (forwardedIps.Length > 0)
            {
                ip = forwardedIps[0];
            }
        }

        if (string.IsNullOrEmpty(ip) && HttpContext.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
        {
            ip = realIp.ToString();
        }

        return ip;
    }
}