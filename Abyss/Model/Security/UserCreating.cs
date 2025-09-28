namespace Abyss.Model.Security;

public class UserCreating
{
    public string Response { get; set; } = "";
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public int Privilege  { get; set; }
}