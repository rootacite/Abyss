namespace Abyss.Model.Media;

public class TaskCreation
{
    public int Type { get; set; }
    public ulong Size { get; set; }
    public string Klass { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author  { get; set; } = "";
}

public class ChipDesc
{
    public uint Id;
    public ulong Addr;
    public ulong Size;
}

public class TaskCreationResponse // As Array
{
    public uint Id;
    public List<ChipDesc> Chips = new();
}