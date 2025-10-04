using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Model.Admin;

namespace Abyss.Components.Services.Admin.Modules;

[Module(100)]
public class HelloModule: IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        return await Task.FromResult(new Ctl
        {
            Head = 200,
            Params = ["Hi"],
        });
    }
}