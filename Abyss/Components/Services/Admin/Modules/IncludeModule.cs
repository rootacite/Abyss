using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Services.Security;
using Abyss.Components.Static;
using Abyss.Model.Admin;
using Abyss.Model.Media;

namespace Abyss.Components.Services.Admin.Modules;

[Module(105)]
public class IncludeModule(
    ILogger<IncludeModule> logger,
    UserService userService,
    ConfigureService configureService,
    ResourceDatabaseService resourceDatabaseService) : IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        // request.Params[0] -> Relative Path
        // request.Params[1] -> Owner Id
        // request.Params[2] -> recursive?
        
        var path = Helpers.SafePathCombine(configureService.MediaRoot, [request.Params[0]]);

        if (request.Params.Length != 3 || !int.TryParse(request.Params[1], out var id))
            return new Ctl
            {
                Head = 400,
                Params = ["Bad Request"]
            };

        if (await userService.QueryUser(id) == null)
            return new Ctl
            {
                Head = 404,
                Params = ["User not found"]
            };

        if (!Directory.Exists(path))
            return new Ctl
            {
                Head = 404,
                Params = ["Directory not found"]
            };
        
        var allPaths = request.Params[2] == "True" ?
            Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Prepend(path)
            : [path];
        var newResources = new List<ResourceAttribute>();
        int c = 0;
        foreach (var p in allPaths)
        {
            var currentPath = Path.GetRelativePath(configureService.MediaRoot, p);
            var uid = ResourceDatabaseService.Uid(currentPath);
            var existing = await resourceDatabaseService.GetResourceAttributeByUidAsync(uid);

            if (existing == null)
            {
                newResources.Add(new ResourceAttribute
                {
                    Uid = uid,
                    Owner = id,
                    Permission = "rw,--,--"
                });
            }
        }

        if (newResources.Any())
        {
            c = await resourceDatabaseService.InsertResourceAttributesAsync(newResources);
            logger.LogInformation(
                $"Successfully initialized {c} new resources under '{path}' for user '{id}'.");
        }
        else
        {
            logger.LogInformation(
                $"No new resources to initialize under '{path}'. All items already exist in the database.");
        }

        return new Ctl
        {
            Head = 200,
            Params = [c.ToString(), "resource(s) add to system"]
        };
    }
}