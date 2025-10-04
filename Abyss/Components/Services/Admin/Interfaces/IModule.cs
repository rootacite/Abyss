using Abyss.Model.Admin;

namespace Abyss.Components.Services.Admin.Interfaces;

public interface IModule
{
    public Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct);
}