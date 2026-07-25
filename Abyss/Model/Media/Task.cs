namespace Abyss.Model.Media;


public class Task
{
    public uint Id { get; set; }
    public int Owner { get; set; }
    public string Class { get; set; }  = "";
    public string Name { get; set; }  = "";
    public string Author  { get; set; } = "";
    public string Group  { get; set; } = "";
}