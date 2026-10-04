namespace UltimateMatchmaker.Server;

public static class Commands
{
    public static readonly Command[] Entries =
    [
        new CmdStop()
        {
            Type = CommandType.Stop,
            Variants =
            [
                "stop", "quit", "exit", "abort"
            ]
        },
        new CmdHelp()
        {
            Type = CommandType.Help,
            Variants =
            [
                "help",
            ]
        }
    ];
    
    public static void ReadCommand()
    {
        while (true)
        {
            var rawInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                continue;
            }

            var rawCommand = rawInput.Split(' ');
            if (rawCommand.Length == 0)
            {
                continue;
            }
            

            if (!TryGetCommand(rawCommand[0], out var command))
            {
                Console.WriteLine($"Unknown command \"{rawCommand[0]}\"");
                if (TryGetCommand("help", out var helpCommand) && helpCommand != null)
                {
                    helpCommand.Execute(out _);
                }

                continue;
            }

            if (command == null)
            {
                continue;
            }

            command.Execute(out var output);
            if (output.Quit)
            {
                return;
            }
        }
    }

    public static bool TryGetCommand(string command, out Command? output)
    {
        foreach (var entry in Entries)
        {
            if (entry.Variants.Contains(command))
            {
                output = entry;
                return true;
            }
        }
        
        output = null;
        return false;
    }
}

public class Command
{
    public CommandType Type { get; set; }
    public string[] Variants { get; set; } = [];

    public virtual void Execute(out ExecuteOutput output)
    {
        output = new ExecuteOutput();
    }
}

public class CmdStop : Command
{
    public override void Execute(out ExecuteOutput output)
    {
        base.Execute(out output);
        output.Quit = true;
    }
}

public class CmdHelp : Command
{
    public override void Execute(out ExecuteOutput output)
    {
        base.Execute(out output);
        Console.WriteLine("Available commands:");
        foreach (var entry in Commands.Entries)
        {
            Console.WriteLine($"{entry.Variants.FirstOrDefault()}");
        }
        output = new ExecuteOutput();
    }
}

public struct ExecuteOutput
{
    public bool Quit { get; set; }
}

public enum CommandType
{
    Unknown,
    Stop,
    Help
}
