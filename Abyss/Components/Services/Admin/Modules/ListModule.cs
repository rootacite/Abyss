using System.Text;
using Abyss.Components.Controllers.Security;
using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Static;
using Abyss.Model.Admin;

namespace Abyss.Components.Services.Admin.Modules;

[Module(107)]
public class ListModule(
    ILogger<ListModule> logger,
    ConfigureService configureService,
    ResourceService resourceService
    ) : IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        // request.Params[0] -> Relative Path
        try
        {
            var path = Helpers.SafePathCombine(configureService.MediaRoot, [request.Params[0]]);

            if (!Directory.Exists(path))
            {
                logger.LogInformation("Directory does not exist: {FullPath}", path);
                return new Ctl
                {
                    Head = 404,
                    Params = ["Not found"]
                };
            }

            var entries = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly).ToArray();
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

                    var permStr = RootController.ConvertToLsPerms(permRaw, isDir);

                    sb.AppendLine($"{permStr} {ownerId,5} {uid} {filename}");
                }
                catch (Exception ex)
                {
                    logger.LogInformation("Error processing entry {Entry}: {ErrorMessage}", entry, ex.Message);
                    // ignored
                }
            }

            logger.LogInformation("Ls operation completed successfully");
            return new Ctl
            {
                Head = 200,
                Params = [sb.ToString()]
            };
        }
        catch (Exception ex)
        {
            logger.LogInformation("Ls operation failed with error: {ErrorMessage}", ex.Message);
            return new Ctl
            {
                Head = 500,
                Params = ["Server Exception", ex.Message]
            };
        }
    }
}