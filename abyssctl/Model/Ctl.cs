using System.Text;
using Newtonsoft.Json;

namespace abyssctl.Model;

public class Ctl
{
    [JsonProperty("head")] public int Head { get; set; }

    [JsonProperty("params")] public string[] Params { get; set; } = [];

    public static string MakeBase64(int head, string[] param)
    {
        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(new Ctl
                    { Head = head, Params = param })));
    }
}