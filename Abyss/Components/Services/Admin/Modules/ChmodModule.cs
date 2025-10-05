using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Abyss.Model.Admin;

namespace Abyss.Components.Services.Admin.Modules;

[Module(106)]
public class ChmodModule(
    ILogger<ChmodModule> logger,
    ConfigureService configureService,
    ResourceDatabaseService resourceDatabaseService
    ) : IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        // request.Params[0] -> Relative Path
        // request.Params[1] -> Permission
        // request.Params[2] -> recursive?
        
        var path = Helpers.SafePathCombine(configureService.MediaRoot, [request.Params[0]]);

        if (request.Params.Length != 3 || !ResourceDatabaseService.PermissionRegex.IsMatch(request.Params[1]))
            return new Ctl
            {
                Head = 400,
                Params = ["Bad Request"]
            };

        if (!Directory.Exists(path))
            return new Ctl
            {
                Head = 404,
                Params = ["Directory not found"]
            };

        var recursive = request.Params[2] == "True";
        
        List<string> targets = new List<string>();
        try
        {
            if (recursive)
            {
                logger.LogInformation($"Recursive directory '{path}'.");
                targets.Add(path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    targets.Add(entry);
                }
            }
            else
            {
                targets.Add(path);
            }

            // Build distinct UIDs
            var relUids = targets
                .Select(t => Path.GetRelativePath(configureService.MediaRoot, t))
                .Select(rel => ResourceDatabaseService.Uid(rel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (relUids.Count == 0)
            {
                logger.LogWarning($"No targets resolved for chmod on '{path}'");
                return new Ctl
                {
                    Head = 304,
                    Params = ["No targets, Not Modified."]
                };
            }

            // Use DatabaseService to perform chunked updates
            var updatedCount = await resourceDatabaseService.UpdatePermissionsByUidsAsync(relUids, request.Params[1] );

            if (updatedCount > 0)
            {
                logger.LogInformation(
                    $"Chmod: updated permissions for {updatedCount} resource(s) (root='{path}', recursive={recursive})");
                return new Ctl
                {
                    Head = 200,
                    Params = ["Ok", updatedCount.ToString()]
                };
            }
            else
            {
                logger.LogWarning($"Chmod: no resources updated for '{path}' (recursive={recursive})");
                return new Ctl
                {
                    Head = 304,
                    Params = ["Not Modified."]
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error changing permissions for: {path}");
            return new Ctl
            {
                Head = 500,
                Params = ["Error", ex.Message]
            };
        }
    }
}