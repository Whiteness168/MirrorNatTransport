using Newtonsoft.Json;

namespace UltimateMatchmaker.Server;

public static class ConfigController
{
    private static string ConfigFilePath => Path.Combine(Environment.CurrentDirectory, "config.json");
    public static Config Config { get; private set; } = new();

    public static void Initialize()
    {
        if (!File.Exists(ConfigFilePath))
        {
            Config = new Config();
            return;
        }
        
        var rawText = File.ReadAllText(ConfigFilePath);
        Config = JsonConvert.DeserializeObject<Config>(rawText) ?? new Config();
    }
}
