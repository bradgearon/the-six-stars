using System;
using System.Collections.Generic;
using CrypticWizard.RandomWordGenerator;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Ui.Services
{
    /// <summary>
    /// Generates a fresh optional seed vocabulary for one writing cycle. The configured weights
    /// are normalized, so 0.50 noun / 0.25 adjective / 0.40 verb becomes the relative selection
    /// probability even though the three values do not add to one.
    /// </summary>
    public sealed class RandomStarWritingSeedWordProvider : IStarWritingSeedWordProvider
    {
        private const int SeedWordCount = 100;
        private const double NounWeight = 0.50;
        private const double AdjectiveWeight = 0.25;
        private const double VerbWeight = 0.40;
        private const int MaximumAttemptsPerWord = 40;

        private readonly bool _enabled;
        private readonly Random _random;
        private readonly WordGenerator? _wordGenerator;
        private readonly object _gate = new object();

        public RandomStarWritingSeedWordProvider(bool enabled, Random? random = null)
        {
            _enabled = enabled;
            _random = random ?? new Random();
            if (_enabled)
            {
                _wordGenerator = new WordGenerator();
                _wordGenerator.SetRandom(_random);
            }
        }

        public IReadOnlyList<string> CreateSeedWords()
        {
            if (!_enabled)
            {
                return Array.Empty<string>();
            }

            lock (_gate)
            {
                WordGenerator wordGenerator = _wordGenerator ?? throw new InvalidOperationException("Seed word generation is disabled.");
                List<string> words = new List<string>(SeedWordCount);
                HashSet<string> uniqueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int maximumAttempts = SeedWordCount * MaximumAttemptsPerWord;

                for (int attempt = 0; words.Count < SeedWordCount && attempt < maximumAttempts; attempt++)
                {
                    WordGenerator.PartOfSpeech partOfSpeech = SelectPartOfSpeech();
                    string? word = wordGenerator.GetWord(partOfSpeech);
                    if (string.IsNullOrWhiteSpace(word))
                    {
                        continue;
                    }

                    string normalized = word.Trim();
                    if (uniqueWords.Add(normalized))
                    {
                        words.Add(normalized);
                    }
                }

                // The bundled dictionaries are large enough that this should rarely run, but keep
                // the request at exactly 100 entries even if cross-part-of-speech duplicates occur.
                while (words.Count < SeedWordCount)
                {
                    string? fallback = wordGenerator.GetWord(SelectPartOfSpeech());
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        words.Add(fallback.Trim());
                    }
                }

                Shuffle(words);
                return words;
            }
        }

        private WordGenerator.PartOfSpeech SelectPartOfSpeech()
        {
            double totalWeight = NounWeight + AdjectiveWeight + VerbWeight;
            double roll = _random.NextDouble() * totalWeight;
            if (roll < NounWeight)
            {
                return WordGenerator.PartOfSpeech.noun;
            }

            roll -= NounWeight;
            if (roll < AdjectiveWeight)
            {
                return WordGenerator.PartOfSpeech.adj;
            }

            return WordGenerator.PartOfSpeech.verb;
        }

        private void Shuffle(IList<string> words)
        {
            for (int index = words.Count - 1; index > 0; index--)
            {
                int swapIndex = _random.Next(index + 1);
                (words[index], words[swapIndex]) = (words[swapIndex], words[index]);
            }
        }
    }
}
