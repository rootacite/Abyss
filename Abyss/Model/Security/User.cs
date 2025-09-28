using SQLite;

namespace Abyss.Model.Security;

[Table("Users")]
public class User
{ 
    [PrimaryKey, AutoIncrement]
    public int Uuid { get; set; }
    [Unique, NotNull]
    public string Username { get; set; } = "";
    [NotNull]
    public int ParentId { get; set; }
    [NotNull]
    public string PublicKey { get; set; } = "";
    [NotNull]
    public int Privilege  { get; set; }
}