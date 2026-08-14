using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>
    /// Optional hand-curated reference work used to anchor the open-ended writing value scale.
    /// Full text is deliberately preserved: these are literary calibration examples, not metadata-only scores.
    /// </summary>
    public sealed class StarWritingValueBaselineExample
    {
        public string Id { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public string Writing { get; set; } = string.Empty;
        public string ReviewDiscipline { get; set; } = StarWritingReviewDisciplines.LiteraryValuesV1;
        public string ValueVector { get; set; } = string.Empty;
        public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public List<StarWritingStoryEmotionValue> EmotionFeedback { get; set; } = new List<StarWritingStoryEmotionValue>();
        public double OverallValue { get; set; }
    }

    public static class StarWritingValueUnits
    {
        public const string Score = "score";
        public const string InformationUnits = "information-units";
        public const string DisplayName = "information units";
        public const string Abbreviation = "IU";
    }

    public sealed class StarWritingValueBaselineFile
    {
        public int Version { get; set; } = 3;
        public string Unit { get; set; } = StarWritingValueUnits.InformationUnits;
        public double ReferenceValue { get; set; } = 100.0;
        public List<StarWritingValueBaselineExample> Examples { get; set; } = new List<StarWritingValueBaselineExample>();
    }

    /// <summary>One prompt-ready reference, either curated from Content or selected from the current shelf.</summary>
    public sealed class StarWritingValueReference
    {
        public string Source { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string StarId { get; set; } = string.Empty;
        public string OutputType { get; set; } = StarWritingOutputTypes.Spark;
        public string Title { get; set; } = string.Empty;
        public string Writing { get; set; } = string.Empty;
        public string ReviewDiscipline { get; set; } = StarWritingReviewDisciplines.LiteraryValuesV1;
        public string ValueVector { get; set; } = string.Empty;
        public List<StarWritingStoryEmotionValue> EmotionFeedback { get; set; } = new List<StarWritingStoryEmotionValue>();
        public string ValueUnit { get; set; } = StarWritingValueUnits.InformationUnits;
        public double OverallValue { get; set; }
        public double PublicValue { get; set; }
    }

    /// <summary>
    /// Loads optional curated baselines from Content/Writing/writing-value-baselines.json.
    /// If it is missing, empty, or malformed, generation continues using the fixed shared IU ladder; review never falls back to actor-local shelf values.
    /// </summary>
    public sealed class StarWritingValueBaselineCatalog
    {
        private readonly string _path;
        private readonly object _gate = new object();
        private DateTime _lastWriteUtc;
        private StarWritingValueBaselineFile _cached = new StarWritingValueBaselineFile();

        private StarWritingValueBaselineCatalog(string path)
        {
            _path = path;
        }

        public static StarWritingValueBaselineCatalog CreateDefault()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string outputPath = Path.Combine(baseDirectory, "Content", "Writing", "writing-value-baselines.json");
            if (File.Exists(outputPath)) return new StarWritingValueBaselineCatalog(outputPath);

            DirectoryInfo? dir = new DirectoryInfo(baseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string sourcePath = Path.Combine(dir.FullName, "src", "Wds.SixStars.Api", "Content", "Writing", "writing-value-baselines.json");
                if (File.Exists(sourcePath)) return new StarWritingValueBaselineCatalog(sourcePath);
            }

            return new StarWritingValueBaselineCatalog(outputPath);
        }

        public double ReferenceValue
        {
            get
            {
                StarWritingValueBaselineFile file = Load();
                return IsFiniteNonNegative(file.ReferenceValue) && file.ReferenceValue > 0
                    ? file.ReferenceValue
                    : 100.0;
            }
        }

        public List<StarWritingValueReference> GetCuratedReferences(string outputType, string preferredStarId, int maximum)
        {
            int take = Math.Max(0, maximum);
            if (take == 0) return new List<StarWritingValueReference>();

            string normalizedOutputType = StarWritingOutputTypes.Normalize(outputType);
            string starId = (preferredStarId ?? string.Empty).Trim();
            List<StarWritingValueReference> references = new List<StarWritingValueReference>();

            foreach (StarWritingValueBaselineExample example in (Load().Examples ?? new List<StarWritingValueBaselineExample>())
                .Where(x => x != null)
                .Where(x => string.Equals(StarWritingReviewDisciplines.Normalize(x.ReviewDiscipline), StarWritingReviewDisciplines.LiteraryValuesV1, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), normalizedOutputType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => string.Equals((x.StarId ?? string.Empty).Trim(), starId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(ReadOverallValue)
                .ThenBy(x => x.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                string vector = NormalizeExampleVector(example);
                if (string.IsNullOrWhiteSpace(vector)
                    || string.IsNullOrWhiteSpace(example.Writing)
                    || !StarWritingEmotionVector.TryNormalizeUsable(vector, out string normalizedVector, out Dictionary<string, StarWritingEmotionValue> values))
                {
                    continue;
                }

                references.Add(new StarWritingValueReference
                {
                    Source = "curated-baseline",
                    Id = string.IsNullOrWhiteSpace(example.Id) ? "curated-" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : example.Id.Trim(),
                    StarId = (example.StarId ?? string.Empty).Trim(),
                    OutputType = normalizedOutputType,
                    Title = (example.Title ?? string.Empty).Trim(),
                    Writing = example.Writing.Trim(),
                    ReviewDiscipline = StarWritingReviewDisciplines.LiteraryValuesV1,
                    ValueVector = normalizedVector,
                    ValueUnit = StarWritingValueUnits.InformationUnits,
                    OverallValue = StarWritingEmotionVector.ReadValue(values, "Overall"),
                    PublicValue = ComputeReferencePublicValue(values)
                });

                if (references.Count >= take) break;
            }

            return references;
        }

        public List<StarWritingValueReference> GetCuratedReaderEmotionReferences(
            string outputType,
            string preferredStarId,
            int maximum)
        {
            int take = Math.Max(0, maximum);
            if (take == 0) return new List<StarWritingValueReference>();

            string normalizedOutputType = StarWritingOutputTypes.Normalize(outputType);
            string starId = (preferredStarId ?? string.Empty).Trim();
            List<StarWritingValueReference> references = new List<StarWritingValueReference>();
            foreach (StarWritingValueBaselineExample example in (Load().Examples ?? new List<StarWritingValueBaselineExample>())
                .Where(x => x != null)
                .Where(x => string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), normalizedOutputType, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(StarWritingReviewDisciplines.Normalize(x.ReviewDiscipline), StarWritingReviewDisciplines.ReaderEmotionIuV1, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => string.Equals((x.StarId ?? string.Empty).Trim(), starId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.OverallValue)
                .ThenBy(x => x.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                string vector = (example.ValueVector ?? string.Empty).Trim();
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(vector, out string normalizedVector, out Dictionary<string, StarWritingEmotionValue> values))
                {
                    vector = StarWritingReaderEmotionVector.FromLegacyEmotionFeedback(example.EmotionFeedback, example.OverallValue);
                    if (!StarWritingReaderEmotionVector.TryNormalizeComplete(vector, out normalizedVector, out values)) continue;
                }
                if (string.IsNullOrWhiteSpace(example.Writing)) continue;

                references.Add(new StarWritingValueReference
                {
                    Source = "curated-baseline",
                    Id = string.IsNullOrWhiteSpace(example.Id) ? "curated-reader-emotion-" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : example.Id.Trim(),
                    StarId = (example.StarId ?? string.Empty).Trim(),
                    OutputType = normalizedOutputType,
                    Title = (example.Title ?? string.Empty).Trim(),
                    Writing = example.Writing.Trim(),
                    ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                    ValueVector = normalizedVector,
                    ValueUnit = StarWritingValueUnits.InformationUnits,
                    OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall"),
                    PublicValue = ComputeReaderEmotionPublicValue(normalizedVector)
                });
                if (references.Count >= take) break;
            }
            return references;
        }

        /// <summary>
        /// Returns the canonical shared reader-emotion calibration set in authored file order.
        /// Review calibration must be actor-independent: no Star preference and no shelf fallback.
        /// </summary>
        public List<StarWritingValueReference> GetSharedReaderEmotionCalibrationReferences(
            string outputType,
            int maximum)
        {
            int take = Math.Max(0, maximum);
            if (take == 0) return new List<StarWritingValueReference>();

            string normalizedOutputType = StarWritingOutputTypes.Normalize(outputType);
            List<StarWritingValueReference> references = new List<StarWritingValueReference>();
            foreach (StarWritingValueBaselineExample example in (Load().Examples ?? new List<StarWritingValueBaselineExample>())
                .Where(x => x != null)
                .Where(x => string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), normalizedOutputType, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(StarWritingReviewDisciplines.Normalize(x.ReviewDiscipline), StarWritingReviewDisciplines.ReaderEmotionIuV1, StringComparison.OrdinalIgnoreCase)))
            {
                string vector = (example.ValueVector ?? string.Empty).Trim();
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(vector, out string normalizedVector, out Dictionary<string, StarWritingEmotionValue> values))
                {
                    vector = StarWritingReaderEmotionVector.FromLegacyEmotionFeedback(example.EmotionFeedback, example.OverallValue);
                    if (!StarWritingReaderEmotionVector.TryNormalizeComplete(vector, out normalizedVector, out values)) continue;
                }
                if (string.IsNullOrWhiteSpace(example.Writing)) continue;

                references.Add(new StarWritingValueReference
                {
                    Source = "shared-calibration",
                    Id = string.IsNullOrWhiteSpace(example.Id)
                        ? "shared-reader-emotion-" + references.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : example.Id.Trim(),
                    StarId = (example.StarId ?? string.Empty).Trim(),
                    OutputType = normalizedOutputType,
                    Title = (example.Title ?? string.Empty).Trim(),
                    Writing = example.Writing.Trim(),
                    ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1,
                    ValueVector = normalizedVector,
                    ValueUnit = StarWritingValueUnits.InformationUnits,
                    OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall"),
                    PublicValue = ComputeReaderEmotionPublicValue(normalizedVector)
                });
                if (references.Count >= take) break;
            }
            return references;
        }

        public static double ComputeReaderEmotionPublicValue(string? valueVector)
        {
            if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                    valueVector,
                    out _,
                    out Dictionary<string, StarWritingEmotionValue> values))
            {
                return -1.0;
            }

            double overall = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
            double strongestEffects = StarWritingReaderEmotionVector.EmotionCategories
                .Select(category => StarWritingReaderEmotionVector.ReadValue(values, category))
                .OrderByDescending(x => x)
                .Take(5)
                .DefaultIfEmpty(0.0)
                .Average();
            return (overall * 0.70) + (strongestEffects * 0.30);
        }

        private StarWritingValueBaselineFile Load()
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    _cached = new StarWritingValueBaselineFile();
                    _lastWriteUtc = DateTime.MinValue;
                    return _cached;
                }

                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(_path);
                if (_lastWriteUtc == lastWriteUtc && _cached != null) return _cached;

                try
                {
                    string json = File.ReadAllText(_path);
                    StarWritingValueBaselineFile? parsed = JsonSerializer.Deserialize<StarWritingValueBaselineFile>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
                    _cached = parsed ?? new StarWritingValueBaselineFile();
                    _cached.Examples ??= new List<StarWritingValueBaselineExample>();
                    _lastWriteUtc = lastWriteUtc;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    // A baseline pack is optional. Never make shelf generation depend on a hand-edited calibration file.
                    _cached = new StarWritingValueBaselineFile();
                    _lastWriteUtc = lastWriteUtc;
                }

                return _cached;
            }
        }

        private static string NormalizeExampleVector(StarWritingValueBaselineExample example)
        {
            string raw = (example.ValueVector ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
            return StarWritingEmotionVector.FormatValues(example.Values ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));
        }

        private static double ReadOverallValue(StarWritingValueBaselineExample example)
        {
            string raw = NormalizeExampleVector(example);
            return StarWritingEmotionVector.TryNormalizeUsable(raw, out _, out Dictionary<string, StarWritingEmotionValue> values)
                ? StarWritingEmotionVector.ReadValue(values, "Overall")
                : -1.0;
        }

        private static double ComputeReferencePublicValue(IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            string[] quality =
            {
                "Memorable", "Original", "Coherent", "Meaningful", "Characterful",
                "EmotionallyResonant", "Polished", "Quotable", "WorthRetelling"
            };
            string[] expressive =
            {
                "Funny", "Deep", "Intrigued", "Interested", "Poetic", "Clever", "Beautiful", "Moving",
                "Witty", "Playful", "Surprising", "Mysterious", "Haunting", "Melancholy", "Hopeful", "Romantic", "Vivid"
            };
            double overall = StarWritingEmotionVector.ReadValue(values, "Overall");
            double qualityAverage = quality.Average(category => StarWritingEmotionVector.ReadValue(values, category));
            double expressiveAverage = expressive
                .Select(category => StarWritingEmotionVector.ReadValue(values, category))
                .OrderByDescending(x => x)
                .Take(3)
                .DefaultIfEmpty(0.0)
                .Average();
            return (overall * 0.40) + (qualityAverage * 0.40) + (expressiveAverage * 0.20);
        }

        private static bool IsFiniteNonNegative(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        }
    }
}
