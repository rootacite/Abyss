namespace Abyss.Model;

public class Video
{
    public string name;
    public ulong duration;
    public List<string> gallery = new();
    public List<Comment> comment = new();
    public bool star;
    public uint like;
    public string author;
}