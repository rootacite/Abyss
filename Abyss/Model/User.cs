namespace Abyss.Model;

public class User
{
    public string Name { get; set; } = "";
    public string Parent { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public int Privilege  { get; set; }
}