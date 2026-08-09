using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Stars;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Api.XPosting
{
    /// <summary>
    /// Selects unposted shelf writings through the Star's LLM voice and publishes them through X.
    /// A started five-post batch is deliberately independent from foreground conversation cancellation.
    /// </summary>
    public sealed class StarXPostingService : IDisposable
    {
        private readonly IStarProfileProvider _profiles;
        private readonly IStarWritingAgent _agent;
        private readonly StarWritingFileStore _writingStore;
        private readonly IXPostClient _xClient;
        private readonly StarXPostingOptions _options;
        private readonly Random _random;
        private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);

        public StarXPostingService(
            IStarProfileProvider profiles,
            IStarWritingAgent agent,
            StarWritingFileStore writingStore,
            IXPostClient xClient,
            StarXPostingOptions options,
            Random? random = null)
        {
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _writingStore = writingStore ?? throw new ArgumentNullException(nameof(writingStore));
            _xClient = xClient ?? throw new ArgumentNullException(nameof(xClient));
            _options = options ?? new StarXPostingOptions();
            _random = random ?? new Random();
        }

        public IReadOnlyList<string> ConfiguredStarIds => (_options.StarAccounts ?? new List<StarXAccountOptions>())
            .Where(x => x != null && x.Enabled && x.HasClientConfiguration && !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => x.Id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public bool HasConfiguredClient(string starId)
        {
            StarXAccountOptions? account = FindAccount(starId);
            return account != null && account.Enabled && account.HasClientConfiguration;
        }

        public async Task<StarXPostingRunResult> PostForStarAsync(string starId)
        {
            // Deliberately no caller cancellation token: after a public-posting batch begins, UI activity,
            // Star selection, and runtime cancel commands must not interrupt or half-mark it.
            await _runGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                StarProfile profile = _profiles.GetProfile(starId);
                StarXAccountOptions? account = FindAccount(profile.StarId);
                if (account == null || !account.Enabled)
                {
                    return CreateMessage(profile.StarId, "No enabled X account entry matches id '" + profile.StarId.ToLowerInvariant() + "'.");
                }
                if (!account.HasClientConfiguration)
                {
                    return CreateMessage(profile.StarId, "The X account for " + profile.StarId + " is missing an OAuth 2.0 ClientId or valid loopback RedirectUri.");
                }
                if (!await _xClient.HasAuthorizationAsync(account, CancellationToken.None).ConfigureAwait(false))
                {
                    return CreateMessage(profile.StarId, "Authorize or reauthorize " + profile.StarId + " for X in the desktop app before posting.");
                }

                StarWritingNotesState state = await _writingStore.LoadAsync(profile.StarId, CancellationToken.None).ConfigureAwait(false);
                List<PersistedStarWriting> unposted = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                    .Where(x => x != null
                        && !x.PostedAt.HasValue
                        && !string.IsNullOrWhiteSpace(x.Id)
                        && !string.IsNullOrWhiteSpace(x.Writing))
                    .ToList();

                int requestedCount = 5;
                int candidateLimit = Math.Max(requestedCount, Math.Min(500, _options.CandidateLimit <= 0 ? 500 : _options.CandidateLimit));
                List<PersistedStarWriting> candidates = TakeRandom(unposted, candidateLimit);
                if (candidates.Count < requestedCount)
                {
                    return new StarXPostingRunResult
                    {
                        StarId = profile.StarId,
                        CandidateCount = candidates.Count,
                        RequestedCount = requestedCount,
                        Message = profile.StarId + " has " + candidates.Count.ToString(CultureInfo.InvariantCulture)
                            + " unposted shelf writing(s); " + requestedCount.ToString(CultureInfo.InvariantCulture) + " are required for one X batch."
                    };
                }

                StarXPostSelectionResult selection;
                try
                {
                    selection = await _agent.PrepareXPostsAsync(
                        profile,
                        state,
                        candidates,
                        requestedCount,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new StarXPostingRunResult
                    {
                        StarId = profile.StarId,
                        CandidateCount = candidates.Count,
                        RequestedCount = requestedCount,
                        Message = "The X selection LLM request failed for " + profile.StarId + ": " + ex.Message
                    };
                }

                List<StarXPostSelectionItem> posts = ValidateSelection(selection, candidates, requestedCount);
                if (posts.Count != requestedCount)
                {
                    return new StarXPostingRunResult
                    {
                        StarId = profile.StarId,
                        CandidateCount = candidates.Count,
                        RequestedCount = requestedCount,
                        Message = "The X selection request for " + profile.StarId + " did not return exactly "
                            + requestedCount.ToString(CultureInfo.InvariantCulture) + " distinct eligible posts with text of 280 characters or fewer."
                    };
                }

                StarXPostingRunResult result = new StarXPostingRunResult
                {
                    StarId = profile.StarId,
                    CandidateCount = candidates.Count,
                    RequestedCount = requestedCount
                };

                for (int index = 0; index < posts.Count; index++)
                {
                    StarXPostSelectionItem planned = posts[index];
                    StarXPostApiResult apiResult;
                    try
                    {
                        apiResult = await _xClient.CreatePostAsync(
                            account,
                            planned.Text,
                            _options.RequestTimeoutSeconds,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        apiResult = new StarXPostApiResult { Error = ex.Message };
                    }

                    StarXPostingRunItemResult item = new StarXPostingRunItemResult
                    {
                        WritingId = planned.WritingId,
                        PostText = planned.Text,
                        Success = apiResult.Success,
                        XPostId = apiResult.PostId,
                        Error = apiResult.Error
                    };
                    result.Items.Add(item);
                    result.AttemptedCount++;

                    if (apiResult.Success)
                    {
                        bool marked = await TryMarkPostedWithRetriesAsync(
                            profile.StarId,
                            planned.WritingId,
                            apiResult.PostId,
                            planned.Text).ConfigureAwait(false);
                        if (marked)
                        {
                            result.PostedCount++;
                        }
                        else
                        {
                            item.Success = false;
                            item.Error = "X accepted the post, but its source writing could not be marked posted in the latest shelf state.";
                            result.FailedCount++;
                        }
                    }
                    else
                    {
                        result.FailedCount++;
                    }

                    if (index + 1 < posts.Count)
                    {
                        int seconds = Math.Max(0, _options.DelayBetweenPostsSeconds);
                        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                    }
                }

                result.BatchCompleted = result.AttemptedCount == requestedCount;
                result.Message = profile.StarId + " completed an X batch: "
                    + result.PostedCount.ToString(CultureInfo.InvariantCulture) + " posted, "
                    + result.FailedCount.ToString(CultureInfo.InvariantCulture) + " failed, from "
                    + result.CandidateCount.ToString(CultureInfo.InvariantCulture) + " randomly sampled unposted writings.";
                return result;
            }
            finally
            {
                _runGate.Release();
            }
        }

        private async Task<bool> TryMarkPostedWithRetriesAsync(
            string starId,
            string writingId,
            string xPostId,
            string postedText)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    bool marked = await _writingStore.TryMarkPostedAsync(
                        starId,
                        writingId,
                        xPostId,
                        postedText,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None).ConfigureAwait(false);
                    if (marked) return true;
                }
                catch when (attempt < 3)
                {
                    // Retry transient file access failures without interrupting the public-posting batch.
                }

                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            return false;
        }

        private StarXAccountOptions? FindAccount(string starId)
        {
            string id = (starId ?? string.Empty).Trim().ToLowerInvariant();
            return (_options.StarAccounts ?? new List<StarXAccountOptions>())
                .FirstOrDefault(x => x != null && string.Equals((x.Id ?? string.Empty).Trim(), id, StringComparison.OrdinalIgnoreCase));
        }

        private List<PersistedStarWriting> TakeRandom(List<PersistedStarWriting> source, int maximum)
        {
            List<PersistedStarWriting> copy = source.ToList();
            for (int i = copy.Count - 1; i > 0; i--)
            {
                int swap = _random.Next(i + 1);
                PersistedStarWriting temp = copy[i];
                copy[i] = copy[swap];
                copy[swap] = temp;
            }
            return copy.Take(maximum).ToList();
        }

        private static List<StarXPostSelectionItem> ValidateSelection(
            StarXPostSelectionResult selection,
            IReadOnlyList<PersistedStarWriting> candidates,
            int requestedCount)
        {
            HashSet<string> eligible = new HashSet<string>(candidates.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<StarXPostSelectionItem> valid = new List<StarXPostSelectionItem>();
            foreach (StarXPostSelectionItem item in selection?.Posts ?? new List<StarXPostSelectionItem>())
            {
                string id = (item?.WritingId ?? string.Empty).Trim();
                string text = (item?.Text ?? string.Empty).Trim();
                if (!eligible.Contains(id) || !seen.Add(id) || text.Length == 0 || text.Length > 280) continue;
                valid.Add(new StarXPostSelectionItem { WritingId = id, Text = text });
                if (valid.Count >= requestedCount) break;
            }
            return valid;
        }

        private static StarXPostingRunResult CreateMessage(string starId, string message)
        {
            return new StarXPostingRunResult { StarId = starId, Message = message };
        }

        public void Dispose()
        {
            _runGate.Dispose();
            if (_xClient is IDisposable disposable) disposable.Dispose();
        }
    }
}
