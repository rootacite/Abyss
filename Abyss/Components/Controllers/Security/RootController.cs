using System.Text;
using Abyss.Components.Services;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Security;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Controllers.Security;

[ApiController]
[Route("api/[controller]")]
public class RootController(ILogger<RootController> logger, UserService userService, ResourceService resourceService)
    : BaseController
{
    [HttpPost("chmod")]
    public async Task<IActionResult> Chmod(string path, string permission, string? recursive)
    {
        logger.LogInformation("Chmod method called with path: {Path}, permission: {Permission}", path, permission);
        
        if (userService.Validate(Token, Ip) != 1)
        {
            logger.LogInformation("Chmod authorization failed for token: {Token}", Token);
            return _401;
        }

        bool r = await resourceService.Chmod(path, Token, permission, Ip, recursive == "true");
        logger.LogInformation("Chmod operation completed with result: {Result}", r);
        return r ? Ok() : StatusCode(500);
    }

    [HttpPost("chown")]
    public async Task<IActionResult> Chown(string path, int owner, string? recursive)
    {
        logger.LogInformation("Chown method called with path: {Path}, owner: {Owner}", path, owner);
        
        if (userService.Validate(Token, Ip) != 1)
        {
            logger.LogInformation("Chown authorization failed for token: {Token}", Token);
            return _401;
        }

        bool r = await resourceService.Chown(path, Token, owner, Ip, recursive == "true");
        logger.LogInformation("Chown operation completed with result: {Result}", r);
        return r ? Ok() : StatusCode(502);
    }

    [HttpGet("ls")]
    public async Task<IActionResult> Ls(string path)
    {
        logger.LogInformation("Ls method called with path: {Path}", path);
        
        if (userService.Validate(Token, Ip) != 1)
        {
            logger.LogInformation("Ls authorization failed for token: {Token}", Token);
            return _401;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogInformation("Ls method received empty path parameter");
            return _400;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            logger.LogInformation("Resolved full path: {FullPath}", fullPath);

            if (!Directory.Exists(fullPath))
            {
                logger.LogInformation("Directory does not exist: {FullPath}", fullPath);
                return _404;
            }

            var entries = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly).ToArray();
            logger.LogInformation("Found {Count} entries in directory", entries.Count());

            var sb = new StringBuilder();

            foreach (var entry in entries)
            {
                try
                {
                    var filename = Path.GetFileName(entry);
                    var isDir = Directory.Exists(entry);

                    var ra = await resourceService.GetAttribute(entry);

                    var ownerId = ra?.Owner ?? -1;
                    var uid = ra?.Uid ?? string.Empty;
                    var permRaw = ra?.Permission ?? "--,--,--";

                    var permStr = ConvertToLsPerms(permRaw, isDir);

                    sb.AppendLine($"{permStr} {ownerId,5} {uid} {filename}");
                }
                catch (Exception ex)
                {
                    logger.LogInformation("Error processing entry {Entry}: {ErrorMessage}", entry, ex.Message);
                    // ignored
                }
            }

            logger.LogInformation("Ls operation completed successfully");
            return Content(sb.ToString(), "text/plain; charset=utf-8");
        }
        catch (Exception ex)
        {
            logger.LogInformation("Ls operation failed with error: {ErrorMessage}", ex.Message);
            return StatusCode(500, "Internal Server Error");
        }
    }

    [HttpPost("init")]
    public async Task<IActionResult> Init(string path, int owner)
    {
        if (userService.Validate(Token, Ip) != 1)
        {
            logger.LogInformation("Init authorization failed for token: {Token}", Token);
            return _401;
        }
        
        var r = await resourceService.Initialize(path, Token, owner, Ip);
        if (r) return Ok(r);
        return _403;
    }

    public static string ConvertToLsPerms(string permRaw, bool isDirectory)
    {
        // expects format like "rw,r-,r-"
        if (string.IsNullOrEmpty(permRaw))
            permRaw = "--,--,--";

        var parts = permRaw.Split(',', StringSplitOptions.None);
        if (parts.Length != 3)
        {
            return (isDirectory ? 'd' : '-') + "---------";
        }

        string MakeTriplet(string token)
        {
            if (token.Length < 2) token = "--";
            var r = token.Length > 0 && token[0] == 'r' ? 'r' : '-';
            var w = token.Length > 1 && token[1] == 'w' ? 'w' : '-';
            var x = '-'; // we don't manage execute bits in current model
            return $"{r}{w}{x}";
        }

        var owner = MakeTriplet(parts[0]);
        var group = MakeTriplet(parts[1]);
        var other = MakeTriplet(parts[2]);

        return (isDirectory ? 'd' : '-') + owner + group + other;
    }
}