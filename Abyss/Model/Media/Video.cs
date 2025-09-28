using Newtonsoft.Json;

namespace Abyss.Model.Media;

public class Video
{
    [JsonProperty("name")] public string Name { get; set; } = "";
    
    [JsonProperty("duration")]
    public ulong Duration { get; set; }
    
    [JsonProperty("gallery")]
    public List<string> Gallery { get; set; } = new();
    
    [JsonProperty("comment")]
    public List<Comment> Comment { get; set; } = new();
    
    [JsonProperty("star")]
    public bool Star { get; set; }

    [JsonProperty("like")] public uint Like { get; set; }

    [JsonProperty("author")] public string Author { get; set; } = "";
    
    [JsonProperty("group")]
    public string? Group { get; set; }
}