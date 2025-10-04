using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Model.Admin;

namespace Abyss.Components.Services.Admin.Modules;

[Module(101)]
public class VersionModule: IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}