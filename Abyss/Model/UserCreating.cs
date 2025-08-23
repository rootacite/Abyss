namespace Abyss.Model;

public class UserCreating
{
    public string Response { get; set; } = "";
    public string Name { get; set; } = "";
    public string Parent { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public int Privilege  { get; set; }
}