using System;
using System.Collections.Generic;
using System.Linq;

namespace Wds.SixStars.Api.Writings
{
    /// <summary>
    /// Controls how many total writing passes run. Pass one is the selected creative method's
    /// two-stage setup plus composition. Passes two and later are reusable critic/rewrite passes.
    /// </summary>
    public sealed class StarWritingPassOptions
    {
        /// <summary>When false, only the selected method's initial generation pass runs.</summary>
        public bool Enabled { get; set; } = true;
        public int MinimumPasses { get; set; } = 1;
        public int MaximumPasses { get; set; } = 6;
        public double OnePassWeight { get; set; } = 0.50;
        public double TwoToThreePassWeight { get; set; } = 0.30;
        public double FourOrMorePassWeight { get; set; } = 0.20;

        public int SelectPassCount(Random random)
        {
            if (!Enabled) return 1;
            if (random == null) throw new ArgumentNullException(nameof(random));

            int minimum = Math.Max(1, MinimumPasses);
            int maximum = Math.Max(minimum, MaximumPasses);
            if (minimum == maximum) return minimum;

            List<PassBucket> buckets = new List<PassBucket>();
            AddBucket(buckets, minimum, maximum, 1, 1, OnePassWeight);
            AddBucket(buckets, minimum, maximum, 2, 3, TwoToThreePassWeight);
            AddBucket(buckets, minimum, maximum, 4, maximum, FourOrMorePassWeight);

            double totalWeight = buckets.Sum(x => x.Weight);
            if (totalWeight <= 0)
            {
                return random.Next(minimum, maximum + 1);
            }

            double roll = random.NextDouble() * totalWeight;
            foreach (PassBucket bucket in buckets)
            {
                if (roll < bucket.Weight)
                {
                    return random.Next(bucket.Minimum, bucket.Maximum + 1);
                }
                roll -= bucket.Weight;
            }

            PassBucket last = buckets[buckets.Count - 1];
            return random.Next(last.Minimum, last.Maximum + 1);
        }

        private static void AddBucket(List<PassBucket> buckets, int configuredMinimum, int configuredMaximum, int bucketMinimum, int bucketMaximum, double weight)
        {
            int minimum = Math.Max(configuredMinimum, bucketMinimum);
            int maximum = Math.Min(configuredMaximum, bucketMaximum);
            if (minimum > maximum || double.IsNaN(weight) || double.IsInfinity(weight) || weight <= 0) return;
            buckets.Add(new PassBucket(minimum, maximum, weight));
        }

        private sealed class PassBucket
        {
            public PassBucket(int minimum, int maximum, double weight)
            {
                Minimum = minimum;
                Maximum = maximum;
                Weight = weight;
            }

            public int Minimum { get; }
            public int Maximum { get; }
            public double Weight { get; }
        }
    }
}
