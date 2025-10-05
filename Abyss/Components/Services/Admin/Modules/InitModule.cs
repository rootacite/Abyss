using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Interfaces;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Services.Security;
using Abyss.Model.Admin;
using Abyss.Model.Security;
using NSec.Cryptography;

namespace Abyss.Components.Services.Admin.Modules;

[Module(103)]
public class InitModule(ILogger<InitModule> logger, UserService userService, ConfigureService configureService, ResourceDatabaseService resourceDatabaseService): IModule
{
    public async Task<Ctl> ExecuteAsync(Ctl request, CancellationToken ct)
    {
        bool empty = await userService.IsEmptyUser();
        if (!empty)
            return new Ctl
            {
                Head = 403,
                Params = ["Access Denied: User list is not empty."]
            };
        
        var key = UserService.GenerateKeyPair();
        string privateKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey));
        string publicKeyBase64 = Convert.ToBase64String(key.Export(KeyBlobFormat.RawPublicKey));

        await userService.AddUserAsync(new User
        {
            Uuid = 1,
            Username = "root",
            ParentId = 1,
            PublicKey = publicKeyBase64,
            Privilege = 1145141919,
        });

        var paths = new string[] { "Tasks", "Live", "Videos", "Images" }
            .Select(x => Path.Combine(configureService.MediaRoot, x));
        foreach (var path in paths)
        {
            if(!Directory.Exists(path))
                Directory.CreateDirectory(path);
            
            var i = await resourceDatabaseService.InsertRaRow(path, 1, "rw,r-,r-", true);
            if (!i)
            {
                logger.LogError("Could not create resource database");
            }
        }

        return new Ctl
        {
            Head = 200,
            Params = [privateKeyBase64]
        };
    }
}