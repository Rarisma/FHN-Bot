using System.Text.Json;

namespace SummariseAnything;
public class Config
{
    public string DiscordAPIKey { get; set; }
    public string GeminiAPIKey { get; set; }
    public string YoutubeAPIKey { get; set; }
    public static Config Load(string path = "/home/rari/config.json") =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path))!;
}