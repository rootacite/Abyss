using Newtonsoft.Json;

namespace Abyss.Model;

public class Bookmark
{
    [JsonProperty("page")]
    public string Page { get; set; } = "";
    [JsonProperty("name")]
    public string Name { get; set; } = "";
}