namespace UltimateMatchmaker.Server;

public class Config
{
    public string ListenIp { get; set; } = "0.0.0.0";
    public string PublicAddress { get; set; } = "194.24.161.46";
    public int ControlPort { get; set; } = 52100;
    public int RelayPortStart { get; set; } = 52101;
    public int RelayPortEnd { get; set; } = 52200;
    public int ClientTimeoutSeconds { get; set; } = 60;
}
