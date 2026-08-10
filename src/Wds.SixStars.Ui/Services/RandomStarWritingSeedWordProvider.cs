using System;
using System.Collections.Generic;
using CrypticWizard.RandomWordGenerator;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Ui.Services
{
    /// <summary>
    /// Generates fresh optional seed vocabularies for writing cycles. Semantic collision keeps
    /// the existing mixed part-of-speech list, while verb/noun displacement receives 100 nouns
    /// so its object-selection step gets a broad noun palette without seeding replacement verbs.
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
                return CreateUniqueWords(SelectPartOfSpeech);
            }
        }

        public IReadOnlyList<string> CreateNounSeedWords()
        {
            if (!_enabled)
            {
                return Array.Empty<string>();
            }

            lock (_gate)
            {
                return CreateUniqueWords(() => WordGenerator.PartOfSpeech.noun);
            }
        }

        private List<string> CreateUniqueWords(Func<WordGenerator.PartOfSpeech> partOfSpeechSelector)
        {
            WordGenerator wordGenerator = _wordGenerator ?? throw new InvalidOperationException("Seed word generation is disabled.");
            List<string> words = new List<string>(SeedWordCount);
            HashSet<string> uniqueWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int maximumAttempts = SeedWordCount * MaximumAttemptsPerWord;

            for (int attempt = 0; words.Count < SeedWordCount && attempt < maximumAttempts; attempt++)
            {
                string? word = wordGenerator.GetWord(partOfSpeechSelector());
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

            // Keep the request at exactly 100 entries even in the extremely unlikely case that
            // the dictionary repeats too often during the uniqueness pass. This mirrors the old
            // semantic-collision behavior while still strongly preferring unique seeds.
            while (words.Count < SeedWordCount)
            {
                string? fallback = wordGenerator.GetWord(partOfSpeechSelector());
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    words.Add(fallback.Trim());
                }
            }

            Shuffle(words);
            return words;
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
