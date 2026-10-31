using Resonator.Core;

try
{
    var options = ParseArgs(args);
    if (options.Help)
    {
        PrintHelp();
        return 0;
    }

    string notation;
    if (!string.IsNullOrWhiteSpace(options.InputPath))
        notation = File.ReadAllText(options.InputPath);
    else if (!string.IsNullOrWhiteSpace(options.Notation))
        notation = options.Notation;
    else if (Console.IsInputRedirected)
        notation = Console.In.ReadToEnd();
    else
    {
        Console.Error.WriteLine("No notation supplied. Use --notation, --input, or stdin.");
        PrintHelp();
        return 2;
    }

    string profilePath = options.ProfilePath
        ?? Environment.GetEnvironmentVariable("RESONATOR_PROFILE")
        ?? Path.Combine(AppContext.BaseDirectory, "resonator.config.json");

    var profile = ProfileLoader.Load(profilePath);
    var generator = new ResonatorMidiGenerator();
    var result = generator.Generate(notation, profile);

    string output = options.OutputPath ?? "resonator.mid";
    output = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllBytes(output, result.MidiBytes);

    Console.WriteLine(output);
    foreach (var warning in result.Warnings)
        Console.Error.WriteLine("warning: " + warning);
    return 0;
}
catch (Exception ex) when (ex is ResonatorParseException or FormatException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static CliOptions ParseArgs(string[] args)
{
    var result = new CliOptions();
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        string Next()
        {
            if (++i >= args.Length) throw new ArgumentException($"Missing value after {arg}.");
            return args[i];
        }

        switch (arg)
        {
            case "-h":
            case "--help": result.Help = true; break;
            case "-n":
            case "--notation": result.Notation = Next(); break;
            case "-i":
            case "--input": result.InputPath = Next(); break;
            case "-o":
            case "--output": result.OutputPath = Next(); break;
            case "-p":
            case "--profile": result.ProfilePath = Next(); break;
            default:
                result.Notation = result.Notation is null ? arg : result.Notation + " " + arg;
                break;
        }
    }
    return result;
}

static void PrintHelp()
{
    Console.WriteLine("""
Resonator CLI - convert Resonator note strings to MIDI

Usage:
  resonator --notation "tempo=120 4/4 | A C E D |" --output song.mid
  resonator --input phrase.res --output phrase.mid
  echo "mode=drums | C E, E, D |" | resonator -o drums.mid

Options:
  -n, --notation <text>   Resonator notation string
  -i, --input <file>      Read notation from a text file
  -o, --output <file>     Output MIDI path (default resonator.mid)
  -p, --profile <file>    Instrument/percussion JSON profile
  -h, --help              Show this help
""");
}

file sealed class CliOptions
{
    public bool Help { get; set; }
    public string? Notation { get; set; }
    public string? InputPath { get; set; }
    public string? OutputPath { get; set; }
    public string? ProfilePath { get; set; }
}
