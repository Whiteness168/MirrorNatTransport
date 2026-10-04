using UltimateMatchmaker.Server.NatPunchthrough;

namespace UltimateMatchmaker.Server;

internal class Program {
    private static RelayServer? _relayServer;
    // private static bool Debug = true;

    private static void Main(string[] args) {
        Console.WriteLine($"Starting UltimateMatchmaker...");
        ConfigController.Initialize();
        Console.WriteLine($"Config initialized.");
        _relayServer = new RelayServer(ConfigController.Config);
        _relayServer.Start();
        Commands.ReadCommand();
        Console.WriteLine($"Stopping UltimateMatchmaker...");
        _relayServer?.Stop();

        // if (Debug)
        // {
        //     args =
        //     [
        //         "client"
        //         // "192.168.142.42:42889"
        //     ];
        // }
        //
        // if (args.Length > 0)
        // {
        //     if (args.Length > 1)
        //     {
        //         Tests.TestClient(args[1]);
        //     }
        //     else
        //     {
        //         if (args[0] == "test1")
        //         {
        //             Tests.StartAsTest1();
        //         }
        //         else
        //         {
        //             Tests.TestClient2();
        //         }
        //     }
        //     Console.ReadLine();
        //     return;
        // }
        //
        // var matchmaker = new Matchmaker.Matchmaker();
        // matchmaker.Start();
        // var relayServer = new RelayServer();
        // relayServer.Start();
        // Console.ReadLine();
        // matchmaker.Stop();
        // relayServer.Stop();
    }
}
