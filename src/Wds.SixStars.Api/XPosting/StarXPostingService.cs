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
        private readonly object _activeRunGate = new object();
        private string _activeStarId = string.Empty;

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

        public string ActiveStarId
        {
            get
            {
                lock (_activeRunGate) return _activeStarId;
            }
        }

        public bool HasConfiguredClient(string starId)
        {
            StarXAccountOptions? account = FindAccount(starId);
            return account != null && account.Enabled && account.HasClientConfiguration;
        }

        public async Task<StarXPostingRunResult> PostForStarAsync(string starId, Action<StarXPostingProgress>? progress = null)
        {
            // Deliberately no caller cancellation token: after a public-posting batch begins, UI activity,
            // Star selection, and runtime cancel commands must not interrupt or half-mark it.
            await _runGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_activeRunGate) _activeStarId = (starId ?? string.Empty).Trim();
            try
            {
                StarProfile profile = _profiles.GetProfile(starId);
                lock (_activeRunGate) _activeStarId = profile.StarId;
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

                ReportProgress(progress, new StarXPostingProgress
                {
                    StarId = profile.StarId,
                    Stage = "selecting",
                    TotalPosts = requestedCount,
                    Message = profile.StarId + " is selecting five writings from "
                        + candidates.Count.ToString(CultureInfo.InvariantCulture) + " X candidates..."
                });

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

                ReportProgress(progress, new StarXPostingProgress
                {
                    StarId = profile.StarId,
                    Stage = "selected",
                    TotalPosts = requestedCount,
                    Message = profile.StarId + " selected " + requestedCount.ToString(CultureInfo.InvariantCulture)
                        + " writings for X. Posting will begin now."
                });

                StarXPostingRunResult result = new StarXPostingRunResult
                {
                    StarId = profile.StarId,
                    CandidateCount = candidates.Count,
                    RequestedCount = requestedCount
                };

                for (int index = 0; index < posts.Count; index++)
                {
                    StarXPostSelectionItem planned = posts[index];
                    int postNumber = index + 1;
                    ReportProgress(progress, new StarXPostingProgress
                    {
                        StarId = profile.StarId,
                        Stage = "posting",
                        CurrentPostNumber = postNumber,
                        TotalPosts = posts.Count,
                        PostedCount = result.PostedCount,
                        FailedCount = result.FailedCount,
                        Message = profile.StarId + " is posting " + postNumber.ToString(CultureInfo.InvariantCulture)
                            + " of " + posts.Count.ToString(CultureInfo.InvariantCulture) + " to X..."
                    });

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

                    if (apiResult.PermissionDenied)
                    {
                        result.PermissionDenied = true;
                        ReportProgress(progress, new StarXPostingProgress
                        {
                            StarId = profile.StarId,
                            Stage = "blocked",
                            CurrentPostNumber = postNumber,
                            TotalPosts = posts.Count,
                            PostedCount = result.PostedCount,
                            FailedCount = result.FailedCount,
                            Message = profile.StarId + " X posting was blocked by X on post "
                                + postNumber.ToString(CultureInfo.InvariantCulture) + " of "
                                + posts.Count.ToString(CultureInfo.InvariantCulture) + ". The remaining posts in this batch were not attempted. "
                                + (string.IsNullOrWhiteSpace(apiResult.Error) ? "HTTP 403 Forbidden." : apiResult.Error.Trim())
                        });
                        break;
                    }

                    if (index + 1 < posts.Count)
                    {
                        int seconds = Math.Max(0, _options.DelayBetweenPostsSeconds);
                        string attemptSummary = item.Success
                            ? profile.StarId + " posted " + postNumber.ToString(CultureInfo.InvariantCulture) + " of "
                                + posts.Count.ToString(CultureInfo.InvariantCulture) + " successfully."
                            : profile.StarId + " post " + postNumber.ToString(CultureInfo.InvariantCulture) + " of "
                                + posts.Count.ToString(CultureInfo.InvariantCulture) + " failed: " + (string.IsNullOrWhiteSpace(item.Error) ? "unknown error" : item.Error.Trim()) + ".";
                        string waitSummary = seconds > 0
                            ? " Waiting " + FormatDelay(seconds) + " before post " + (postNumber + 1).ToString(CultureInfo.InvariantCulture)
                                + " of " + posts.Count.ToString(CultureInfo.InvariantCulture) + "."
                            : " Continuing to post " + (postNumber + 1).ToString(CultureInfo.InvariantCulture) + " of "
                                + posts.Count.ToString(CultureInfo.InvariantCulture) + ".";

                        ReportProgress(progress, new StarXPostingProgress
                        {
                            StarId = profile.StarId,
                            Stage = seconds > 0 ? "waiting" : "posted",
                            CurrentPostNumber = postNumber,
                            TotalPosts = posts.Count,
                            PostedCount = result.PostedCount,
                            FailedCount = result.FailedCount,
                            DelaySeconds = seconds,
                            Message = attemptSummary + waitSummary
                        });

                        if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                    }
                    else
                    {
                        ReportProgress(progress, new StarXPostingProgress
                        {
                            StarId = profile.StarId,
                            Stage = item.Success ? "posted" : "failed",
                            CurrentPostNumber = postNumber,
                            TotalPosts = posts.Count,
                            PostedCount = result.PostedCount,
                            FailedCount = result.FailedCount,
                            Message = item.Success
                                ? profile.StarId + " posted " + postNumber.ToString(CultureInfo.InvariantCulture) + " of "
                                    + posts.Count.ToString(CultureInfo.InvariantCulture) + " successfully."
                                : profile.StarId + " post " + postNumber.ToString(CultureInfo.InvariantCulture) + " of "
                                    + posts.Count.ToString(CultureInfo.InvariantCulture) + " failed: "
                                    + (string.IsNullOrWhiteSpace(item.Error) ? "unknown error" : item.Error.Trim()) + "."
                        });
                    }
                }

                result.BatchCompleted = result.AttemptedCount == requestedCount;
                result.Message = result.PermissionDenied
                    ? profile.StarId + " stopped its X batch after X returned HTTP 403. "
                        + result.PostedCount.ToString(CultureInfo.InvariantCulture) + " post(s) were published before the denial; "
                        + (requestedCount - result.AttemptedCount).ToString(CultureInfo.InvariantCulture)
                        + " remaining selected post(s) were left unattempted and remain eligible for a future batch."
                    : profile.StarId + " completed an X batch: "
                        + result.PostedCount.ToString(CultureInfo.InvariantCulture) + " posted, "
                        + result.FailedCount.ToString(CultureInfo.InvariantCulture) + " failed, from "
                        + result.CandidateCount.ToString(CultureInfo.InvariantCulture) + " randomly sampled unposted writings.";
                return result;
            }
            finally
            {
                lock (_activeRunGate) _activeStarId = string.Empty;
                _runGate.Release();
            }
        }

        private static void ReportProgress(Action<StarXPostingProgress>? progress, StarXPostingProgress value)
        {
            if (progress == null) return;
            try
            {
                progress(value);
            }
            catch
            {
                // Status reporting is best-effort and must never interrupt a public-posting batch.
            }
        }

        private static string FormatDelay(int seconds)
        {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (delay.TotalMinutes >= 1)
            {
                return ((int)delay.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":"
                    + delay.Seconds.ToString("00", CultureInfo.InvariantCulture);
            }
            return delay.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
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
