using System.Text;
using Abyss.Components.Services;
using Abyss.Components.Static;
using Microsoft.AspNetCore.Mvc;

namespace Abyss.Components.Controllers.Security;

[ApiController]
[Route("api/[controller]")]
public class RootController(ILogger<RootController> logger, UserService userService, ResourceService resourceService)
    : BaseController
{
    [HttpPost("chmod")]
    public async Task<IActionResult> Chmod(string token, string path, string permission)
    {
        logger.LogInformation("Chmod method called with path: {Path}, permission: {Permission}", path, permission);
        
        if (userService.Validate(token, Ip) != 1)
        {
            logger.LogInformation("Chmod authorization failed for token: {Token}", token);
            return StatusCode(401, "Unauthorized");
        }

        bool r = await resourceService.Chmod(path, token, permission, Ip, true);
        logger.LogInformation("Chmod operation completed with result: {Result}", r);
        return r ? Ok() : StatusCode(502);
    }

    [HttpPost("chown")]
    public async Task<IActionResult> Chown(string token, string path, int owner)
    {
        logger.LogInformation("Chown method called with path: {Path}, owner: {Owner}", path, owner);
        
        if (userService.Validate(token, Ip) != 1)
        {
            logger.LogInformation("Chown authorization failed for token: {Token}", token);
            return StatusCode(401, "Unauthorized");
        }

        bool r = await resourceService.Chown(path, token, owner, Ip, true);
        logger.LogInformation("Chown operation completed with result: {Result}", r);
        return r ? Ok() : StatusCode(502);
    }

    [HttpGet("ls")]
    public async Task<IActionResult> Ls(string token, string path)
    {
        logger.LogInformation("Ls method called with path: {Path}", path);
        
        if (userService.Validate(token, Ip) != 1)
        {
            logger.LogInformation("Ls authorization failed for token: {Token}", token);
            return StatusCode(401, "Unauthorized");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            logger.LogInformation("Ls method received empty path parameter");
            return BadRequest("path is required");
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            logger.LogInformation("Resolved full path: {FullPath}", fullPath);

            if (!Directory.Exists(fullPath))
            {
                logger.LogInformation("Directory does not exist: {FullPath}", fullPath);
                return BadRequest("Path does not exist or is not a directory");
            }

            var entries = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly);
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

    private static string ConvertToLsPerms(string permRaw, bool isDirectory)
    {
        // expects format like "rw,r-,r-"
        if (string.IsNullOrEmpty(permRaw))
            permRaw = "--,--,--";

        var parts = permRaw.Split(',', StringSplitOptions.None);
        if (parts.Length != 3)
        {
            return (isDirectory ? 'd' : '-') + "---------";
        }

        string makeTriplet(string token)
        {
            if (token.Length < 2) token = "--";
            var r = token.Length > 0 && token[0] == 'r' ? 'r' : '-';
            var w = token.Length > 1 && token[1] == 'w' ? 'w' : '-';
            var x = '-'; // we don't manage execute bits in current model
            return $"{r}{w}{x}";
        }

        var owner = makeTriplet(parts[0]);
        var group = makeTriplet(parts[1]);
        var other = makeTriplet(parts[2]);

        return (isDirectory ? 'd' : '-') + owner + group + other;
    }
}