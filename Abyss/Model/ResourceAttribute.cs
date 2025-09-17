using SQLite;

namespace Abyss.Model;

[Table("ResourceAttributes")]
public class ResourceAttribute
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }
    
    [Unique, NotNull]
    public string Uid { get; init; } = "@";
    [NotNull]
    public int Owner { get; set; }
    [NotNull]
    public string Permission { get; set; } = "--,--,--";
}