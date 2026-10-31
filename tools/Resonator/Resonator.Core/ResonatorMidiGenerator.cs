namespace Resonator.Core;

public sealed class ResonatorMidiGenerator
{
    public GenerateResult Generate(string notation, ResonatorProfile? profile = null)
    {
        profile ??= new ResonatorProfile();
        var parser = new ResonatorParser(profile);
        var parsed = parser.Parse(notation);
        byte[] midi = MidiFileWriter.Write(parsed.Composition, profile);
        return new GenerateResult(midi, parsed.Composition, parsed.Warnings);
    }

    public void GenerateToFile(string notation, string outputPath, ResonatorProfile? profile = null)
    {
        var result = Generate(notation, profile);
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, result.MidiBytes);
    }
}

public sealed record GenerateResult(
    byte[] MidiBytes,
    ResonatorComposition Composition,
    IReadOnlyList<string> Warnings);
