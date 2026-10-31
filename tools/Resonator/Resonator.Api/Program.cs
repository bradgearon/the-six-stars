using Resonator.Core;

var builder = WebApplication.CreateBuilder(args);

string profilePath = builder.Configuration["Resonator:ProfilePath"]
    ?? Environment.GetEnvironmentVariable("RESONATOR_PROFILE")
    ?? Path.Combine(AppContext.BaseDirectory, "resonator.config.json");

if (!Path.IsPathRooted(profilePath)) profilePath = Path.Combine(AppContext.BaseDirectory, profilePath);
var profile = ProfileLoader.Load(profilePath);
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton<ResonatorMidiGenerator>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Resonator API",
    endpoints = new[] { "POST /midi", "POST /midi/text", "POST /parse" }
}));

app.MapPost("/midi", (GenerateMidiRequest request, ResonatorMidiGenerator generator, ResonatorProfile p) =>
{
    try
    {
        var result = generator.Generate(request.Notation, p);
        string fileName = string.IsNullOrWhiteSpace(request.FileName) ? "resonator.mid" : EnsureMidiExtension(request.FileName);
        return Results.File(result.MidiBytes, "audio/midi", fileName);
    }
    catch (ResonatorParseException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FormatException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/midi/text", async (HttpRequest request, ResonatorMidiGenerator generator, ResonatorProfile p) =>
{
    using var reader = new StreamReader(request.Body);
    string notation = await reader.ReadToEndAsync();
    try
    {
        var result = generator.Generate(notation, p);
        return Results.File(result.MidiBytes, "audio/midi", "resonator.mid");
    }
    catch (Exception ex) when (ex is ResonatorParseException or FormatException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/parse", (GenerateMidiRequest request, ResonatorProfile p) =>
{
    try
    {
        var parsed = new ResonatorParser(p).Parse(request.Notation);
        return Results.Ok(new
        {
            parsed.Composition.Tempo,
            parsed.Composition.Numerator,
            parsed.Composition.Denominator,
            parsed.Composition.Ppq,
            parsed.Composition.LengthTicks,
            eventCount = parsed.Composition.Events.Count,
            warnings = parsed.Warnings
        });
    }
    catch (Exception ex) when (ex is ResonatorParseException or FormatException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

static string EnsureMidiExtension(string fileName) =>
    fileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".mid";

public sealed record GenerateMidiRequest(string Notation, string? FileName = null);
