using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Wds.SixStars.Api.Audio
{
    public sealed class FuzzyWakeWordDetector : IWakeWordDetector
    {
        public Task<string?> TryDetectStarAsync(string transcribedText, WakeAliasConfig config, CancellationToken cancellationToken)
        {
            string text = (transcribedText ?? string.Empty).ToLowerInvariant();
            HashSet<string>? equipped = config.EquippedStars.Count == 0
                ? null
                : new HashSet<string>(config.EquippedStars, StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, List<string>> item in config.WakeAliases)
            {
                if (equipped != null && !equipped.Contains(item.Key))
                {
                    continue;
                }

                foreach (string alias in item.Value.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (text.IndexOf(alias.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return Task.FromResult<string?>(item.Key);
                    }
                }
            }

            return Task.FromResult<string?>(null);
        }
    }
}
