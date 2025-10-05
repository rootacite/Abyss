using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Components.Services.Security;
using Abyss.Model.Admin;
using Abyss.Model.Security;
using NSec.Cryptography;

namespace Abyss.Components.Services.Admin.Modules;

[Module(104)]
public class UserAddModule(UserService userService): IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        // request.Params[0] -> Username
        // request.Params[1] -> Privilege
        
        if (request.Params.Length != 2 || !UserService.IsAlphanumeric(request.Params[0]) || !int.TryParse(request.Params[1], out var privilege))
            return new Ctl
            {
                Head = 400,
                Params = ["Bad Request"]
            };

        if (await userService.QueryUser(request.Params[0]) != null)
            return new Ctl
            {
                Head = 403,
                Params = ["User exists"]
            };

        var key = UserService.GenerateKeyPair();
        string privateKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey));
        string publicKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPublicKey));
        
        await userService.AddUserAsync(new User
        {
            Username = request.Params[0],
            ParentId = 1,
            PublicKey = publicKeyBase64,
            Privilege = privilege,
        });
        
        return new Ctl
        {
            Head = 200,
            Params = [privateKeyBase64]
        };
    }
}