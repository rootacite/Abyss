using Newtonsoft.Json;

namespace Abyss.Model;

public class Comic
{
    [JsonProperty("comic_name")]
    public string ComicName { get; set; } = "";
    [JsonProperty("page_count")]
    public int PageCount { get; set; }
    [JsonProperty("bookmarks")]
    public List<Bookmark> Bookmarks { get; set; } = new();
    [JsonProperty("author")]
    public string Author { get; set; } = ""; 
    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();
    [JsonProperty("list")]
    public List<string> List { get; set; } = new();
}