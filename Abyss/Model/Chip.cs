namespace Abyss.Model;

public enum ChipState
{
    Created,
    Uploaded,
    Verified
}

public class Chip
{
    public uint Id { get; set; }
    public ulong Addr { get; set; }
    public ulong Size { get; set; }
    public string Hash { get; set; } = "";
    public ChipState State { get; set; }
}