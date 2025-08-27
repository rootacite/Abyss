namespace Abyss.Model;

public enum TaskType
{
    Video = 1,
    Image = 2,
}

public class Task
{
    public uint Id;
    public string Owner = "";
    public string Class = "";
    public string Name = "";
    public TaskType Type;
}