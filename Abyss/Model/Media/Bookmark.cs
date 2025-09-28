using Newtonsoft.Json;

namespace Abyss.Model.Media;

public class Bookmark
{
    [JsonProperty("page")]
    public string Page { get; set; } = "";
    [JsonProperty("name")]
    public string Name { get; set; } = "";
}