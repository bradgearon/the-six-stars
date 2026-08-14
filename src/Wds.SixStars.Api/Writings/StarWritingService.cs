using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Writings
{
    public sealed class StarWritingService
    {
        private const string GlobalOverallRuleId = "global-overall-excellence";
        private const double DefaultGlobalOverallValueThreshold = 80.0;
        private const int SparkPerformanceElitePoolLimit = 100;
        private const int SparkPerformanceCandidateLimit = 25;
        private const int SparkPerformanceTopAnchorCount = 5;
        private const int SparkPerformanceContrastCount = 2;
        private const double SparkPerformanceContrastBandStart = 0.60;
        private static readonly double GlobalOverallValueThreshold = ReadGlobalOverallValueThreshold();

        private readonly IStarProfileProvider _profiles;
        private readonly IStarWritingAgent _agent;
        private readonly StarWritingFileStore _store;
        private readonly StarWritingPassOptions _passOptions;
        private readonly StarWritingMethodOptions _methodOptions;
        private readonly IStarWritingSeedWordProvider? _seedWordProvider;
        private readonly Random _random;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);

        public StarWritingService(
            IStarProfileProvider profiles,
            IStarWritingAgent agent,
            StarWritingFileStore store,
            StarWritingPassOptions? passOptions = null,
            StarWritingMethodOptions? methodOptions = null,
            IStarWritingSeedWordProvider? seedWordProvider = null,
            Random? random = null)
        {
            _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
            _agent = agent ?? throw new ArgumentNullException(nameof(agent));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _passOptions = passOptions ?? new StarWritingPassOptions();
            _methodOptions = methodOptions ?? new StarWritingMethodOptions();
            _seedWordProvider = seedWordProvider;
            _random = random ?? new Random();
        }

        public string RootDirectory => _store.RootDirectory;

        public Task<StarWritingNotesState> GetStateAsync(string starId, CancellationToken cancellationToken)
        {
            return _store.LoadAsync(starId, cancellationToken);
        }

        public Task<StarWritingCreateResult> GenerateAndReviewAsync(
            string starId,
            CancellationToken cancellationToken,
            Action<string>? progress = null)
        {
            return GenerateAndReviewAsync(starId, null, cancellationToken, progress);
        }

        /// <summary>
        /// Forge-facing generation entry point. requestedWritingMethod=null keeps the existing weighted automatic
        /// selection; supplying one of the three stable method ids targets that generator explicitly.
        /// </summary>
        public async Task<StarWritingCreateResult> GenerateAndReviewAsync(
            string starId,
            string? requestedWritingMethod,
            CancellationToken cancellationToken,
            Action<string>? progress = null)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                System.Diagnostics.Stopwatch totalTimer = System.Diagnostics.Stopwatch.StartNew();
                StarProfile profile = _profiles.GetProfile(starId);
                StarWritingNotesState state = await _store.LoadAsync(profile.StarId, cancellationToken).ConfigureAwait(false);
                string writingMethod = string.IsNullOrWhiteSpace(requestedWritingMethod)
                    ? _methodOptions.SelectMethod(_random)
                    : StarWritingMethods.Normalize(requestedWritingMethod);
                ReportProgress(progress, profile.StarId + " writing shelf: selected " + writingMethod + ".");
                System.Diagnostics.Debug.WriteLine(
                    "[StarWritingMethod] " + profile.StarId + " selected " + writingMethod + ".");

                // Do not make a new-writing button repair legacy vectors first. Older malformed
                // ratings are historical data and must never block or add hidden LLM calls to a
                // fresh generation. They can be migrated by a separate maintenance action later.
                int repairedRatingCount = 0;

                if (string.Equals(writingMethod, StarWritingMethods.ConstellationForge, StringComparison.OrdinalIgnoreCase))
                {
                    return await GenerateConstellationStoryAsync(profile, state, repairedRatingCount, progress, cancellationToken).ConfigureAwait(false);
                }

                int selectedPassCount = _passOptions.SelectPassCount(_random);
                System.Diagnostics.Debug.WriteLine(
                    "[StarWritingPasses] " + profile.StarId + " selected " + selectedPassCount.ToString(CultureInfo.InvariantCulture) + " total Spark pass(es).");

                // Pass one is always a two-stage creative method. Semantic collision privately
                // chooses unrelated word pairs; verb/noun displacement privately chooses objects
                // and their ordinary actions before making a deliberately displaced completion.
                long generationStartedAt = totalTimer.ElapsedMilliseconds;
                IReadOnlyList<string> seedWords = string.Equals(
                        writingMethod,
                        StarWritingMethods.VerbNounDisplacement,
                        StringComparison.OrdinalIgnoreCase)
                    ? (_seedWordProvider?.CreateNounSeedWords() ?? Array.Empty<string>())
                    : (_seedWordProvider?.CreateSeedWords() ?? Array.Empty<string>());
                StarWritingGenerationResult generation = await _agent.GenerateWritingCandidatesAsync(profile, state, seedWords, writingMethod, cancellationToken).ConfigureAwait(false);
                generation.WritingMethod = StarWritingMethods.Normalize(generation.WritingMethod);
                generation.OutputType = StarWritingMethods.GetOutputType(generation.WritingMethod);
                NormalizeGeneration(generation);
                if (string.Equals(generation.WritingMethod, StarWritingMethods.SemanticCollision, StringComparison.OrdinalIgnoreCase))
                {
                    RecordGeneratedWordSet(state, generation.UsedWords);
                }
                else if (string.Equals(generation.WritingMethod, StarWritingMethods.VerbNounDisplacement, StringComparison.OrdinalIgnoreCase))
                {
                    RecordGeneratedNouns(state, generation.UsedWords.Select(words => words.FirstOrDefault() ?? string.Empty));
                }
                generation.SelectedPassCount = selectedPassCount;
                generation.CompletedPassCount = 1;
                AppendPassSnapshot(generation, 1, 0);
                await SaveLatestPassStateAsync(state, generation).ConfigureAwait(false);

                // Passes two and later reuse the selected method's critic/rewrite contract. Each proposed
                // replacement includes an old and new value. Only a strictly higher-valued rewrite
                // becomes the input state for the next pass; ties and lower values keep the old item.
                for (int passNumber = 2; passNumber <= selectedPassCount; passNumber++)
                {
                    StarWritingImprovementResult improvement = await _agent
                        .ImproveWritingCandidatesAsync(profile, state, generation, passNumber, selectedPassCount, cancellationToken)
                        .ConfigureAwait(false);
                    int applied = ApplyImprovementReplacements(generation, improvement);
                    generation.AppliedImprovementCount += applied;
                    generation.CompletedPassCount = passNumber;
                    generation.RawModelJson += Environment.NewLine
                        + "--- WRITING IMPROVEMENT PASS " + passNumber.ToString(CultureInfo.InvariantCulture) + " ---" + Environment.NewLine
                        + improvement.RawModelJson;
                    AppendPassSnapshot(generation, passNumber, applied);
                    await SaveLatestPassStateAsync(state, generation).ConfigureAwait(false);
                }

                long generationFinishedAt = totalTimer.ElapsedMilliseconds;
                StarWritingReviewResult review = await _agent.RateWritingCandidatesAsync(profile, state, generation, cancellationToken).ConfigureAwait(false);
                NormalizeReview(review);
                long reviewFinishedAt = totalTimer.ElapsedMilliseconds;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                StarWritingHistoryBatch batch = new StarWritingHistoryBatch
                {
                    Id = "writing-batch-" + Guid.NewGuid().ToString("N"),
                    WritingMethod = generation.WritingMethod,
                    OutputType = generation.OutputType,
                    GeneratedAt = now,
                    SelectedPassCount = generation.SelectedPassCount,
                    CompletedPassCount = generation.CompletedPassCount,
                    AppliedImprovementCount = generation.AppliedImprovementCount,
                    Passes = ClonePasses(generation.Passes)
                };

                int persistedCount = 0;
                for (int i = 0; i < generation.Items.Count; i++)
                {
                    StarWritingCandidate candidate = generation.Items[i];
                    StarWritingRating rating = review.Ratings.First(x => x.Index == i);
                    if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                            rating.EmotionalVector,
                            out string readerEmotionVector,
                            out Dictionary<string, StarWritingEmotionValue> values))
                    {
                        throw new InvalidOperationException("Spark review returned an incomplete ReaderEmotionIuV1 vector.");
                    }
                    rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
                    rating.EmotionalVector = readerEmotionVector;
                    rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
                    rating.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                    List<string> matchedRules = GetMatchedRuleIds(profile, values);
                    string itemId = "writing-" + Guid.NewGuid().ToString("N");
                    StarWritingHistoryItem historyItem = new StarWritingHistoryItem
                    {
                        Id = itemId,
                        WritingMethod = generation.WritingMethod,
                        OutputType = generation.OutputType,
                        Title = generation.Title,
                        ContributingStarIds = (generation.ContributingStarIds ?? new List<string>()).ToList(),
                        SourceBatchId = batch.Id,
                        GeneratedAt = now,
                        Index = i,
                        WordsUsed = candidate.WordsUsed.ToList(),
                        Writing = candidate.Writing,
                        ReviewDiscipline = rating.ReviewDiscipline,
                        EmotionalVector = rating.EmotionalVector,
                        EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                        OverallValue = rating.OverallValue,
                        Persisted = matchedRules.Count > 0,
                        MatchedPersistenceRuleIds = matchedRules
                    };
                    batch.Items.Add(historyItem);

                    if (historyItem.Persisted)
                    {
                        state.PersistedWritings.Add(new PersistedStarWriting
                        {
                            Id = itemId,
                            WritingMethod = generation.WritingMethod,
                            OutputType = generation.OutputType,
                            Title = generation.Title,
                            ContributingStarIds = (generation.ContributingStarIds ?? new List<string>()).ToList(),
                            SourceBatchId = batch.Id,
                            WordsUsed = historyItem.WordsUsed.ToList(),
                            Writing = historyItem.Writing,
                            ReviewDiscipline = historyItem.ReviewDiscipline,
                            EmotionalVector = historyItem.EmotionalVector,
                            EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                            OverallValue = historyItem.OverallValue,
                            MatchedPersistenceRuleIds = matchedRules.ToList(),
                            GeneratedAt = now,
                            PersistedAt = now,
                            ToldCount = 0
                        });
                        persistedCount++;
                    }
                }

                state.RecentWritingHistory.AddRange(batch.Items);
                state.RecentWritingHistory = state.RecentWritingHistory
                    .OrderByDescending(x => x.GeneratedAt)
                    .Take(10)
                    .OrderBy(x => x.GeneratedAt)
                    .ThenBy(x => x.Index)
                    .ToList();
                RebuildUsedWordsFromPersistedWritings(state);
                await _store.SaveGeneratedBatchAsync(state, profile.DisplayName, batch, CancellationToken.None).ConfigureAwait(false);
                long saveFinishedAt = totalTimer.ElapsedMilliseconds;
                System.Diagnostics.Debug.WriteLine(
                    "[StarWritingTiming] " + profile.StarId
                    + " passes=" + generation.CompletedPassCount + "/" + generation.SelectedPassCount
                    + ", improvements=" + generation.AppliedImprovementCount
                    + ", generation=" + (generationFinishedAt - generationStartedAt) + "ms"
                    + ", review=" + (reviewFinishedAt - generationFinishedAt) + "ms"
                    + ", saveAndRules=" + (saveFinishedAt - reviewFinishedAt) + "ms"
                    + ", total=" + saveFinishedAt + "ms.");

                return new StarWritingCreateResult
                {
                    WritingMethod = generation.WritingMethod,
                    OutputType = generation.OutputType,
                    State = state,
                    Batch = batch,
                    PersistedCount = persistedCount,
                    RepairedRatingCount = repairedRatingCount,
                    SelectedPassCount = generation.SelectedPassCount,
                    CompletedPassCount = generation.CompletedPassCount,
                    AppliedImprovementCount = generation.AppliedImprovementCount
                };
            }
            finally { _operationGate.Release(); }
        }

        private async Task<StarWritingCreateResult> GenerateConstellationStoryAsync(
            StarProfile viewpointProfile,
            StarWritingNotesState state,
            int repairedRatingCount,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            List<StarProfile> allProfiles = (_profiles.GetAllProfiles() ?? Array.Empty<StarProfile>())
                .Where(x => x != null
                    && x.Active
                    && (x.Soul == null || x.Soul.Writing)
                    && !string.IsNullOrWhiteSpace(x.StarId))
                .GroupBy(x => x.StarId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            if (!allProfiles.Any(x => string.Equals(x.StarId, viewpointProfile.StarId, StringComparison.OrdinalIgnoreCase)))
            {
                allProfiles.Insert(0, viewpointProfile);
            }
            if (allProfiles.Count < 3)
            {
                throw new InvalidOperationException("Constellation Forge needs at least three active writing-enabled Stars: one viewpoint Star plus two collaborators so a different Star can independently audit a pending finish.");
            }

            List<StarProfile> lightPassProfiles = new List<StarProfile> { viewpointProfile };
            lightPassProfiles.AddRange(ShuffleProfiles(allProfiles
                .Where(x => !string.Equals(x.StarId, viewpointProfile.StarId, StringComparison.OrdinalIgnoreCase))));

            StarWritingStoryDevelopmentState development = new StarWritingStoryDevelopmentState
            {
                Id = "constellation-development-" + Guid.NewGuid().ToString("N"),
                ViewpointStarId = viewpointProfile.StarId,
                UpdatedAt = startedAt
            };
            state.LatestStoryDevelopment = development;
            state.LatestWritingMethod = StarWritingMethods.ConstellationForge;
            state.LatestWritingPasses = new List<StarWritingPassSnapshot>();
            state.LatestSelectedPassCount = 1;
            state.LatestCompletedPassCount = 0;
            state.LatestAppliedImprovementCount = 0;
            state.LatestPassUpdatedAt = startedAt;
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);

            List<string> subjects = new List<string>();
            List<string> verbs = new List<string>();
            List<StarWritingStoryWish> storyWishes = new List<StarWritingStoryWish>();
            int phaseNumber = 0;

            // Light pass: every active Star contributes fresh subjects, verbs, and five current story wishes before anyone writes prose.
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: starting the " + lightPassProfiles.Count.ToString(CultureInfo.InvariantCulture) + "-Star light seed pass (vocabulary + what each Star wants to hear)." );
            int lightPassIndex = 0;
            foreach (StarProfile termProfile in lightPassProfiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lightPassIndex++;
                ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: vocabulary "
                    + lightPassIndex.ToString(CultureInfo.InvariantCulture) + "/" + lightPassProfiles.Count.ToString(CultureInfo.InvariantCulture)
                    + " — " + termProfile.StarId + " is choosing fresh subjects, verbs, and five story wishes.");
                StarWritingStoryTermContributionResult contribution;
                try
                {
                    contribution = await _agent.ContributeStoryTermsAsync(
                        termProfile,
                        state,
                        _seedWordProvider?.CreateNounSeedWords() ?? Array.Empty<string>(),
                        _seedWordProvider?.CreateVerbSeedWords() ?? Array.Empty<string>(),
                        subjects,
                        verbs,
                        storyWishes,
                        phaseNumber + 1,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    throw new InvalidOperationException(
                        "Constellation Forge light seed pass failed for " + termProfile.StarId
                        + ". The model must return subjects, verbs, and exactly five story wishes; fix the generation contract rather than skipping this actor.",
                        ex);
                }

                List<string> newSubjects = AddUniqueTerms(subjects, contribution.Subjects);
                List<string> newVerbs = AddUniqueTerms(verbs, contribution.Verbs);
                List<StarWritingStoryWish> newWishes = (contribution.StoryWishes ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => new StarWritingStoryWish
                    {
                        SourceActorId = string.IsNullOrWhiteSpace(termProfile.ActorId) ? termProfile.StarId : termProfile.ActorId.Trim(),
                        SourceDisplayName = string.IsNullOrWhiteSpace(termProfile.DisplayName) ? termProfile.StarId : termProfile.DisplayName.Trim(),
                        SourceActorType = string.IsNullOrWhiteSpace(termProfile.ActorType) ? "star" : termProfile.ActorType.Trim(),
                        Wish = x.Trim()
                    })
                    .Take(5)
                    .ToList();
                storyWishes.AddRange(newWishes);
                RecordStoryTerms(state, newSubjects, newVerbs);
                phaseNumber++;
                development.CurrentPhase = phaseNumber;
                development.Subjects = subjects.ToList();
                development.Verbs = verbs.ToList();
                development.StoryWishes = CloneStoryWishes(storyWishes);
                development.UpdatedAt = DateTimeOffset.UtcNow;
                development.Phases.Add(new StarWritingStoryDevelopmentPhase
                {
                    PhaseNumber = phaseNumber,
                    Stage = "light-terms",
                    StarId = termProfile.StarId,
                    StoryWishes = CloneStoryWishes(newWishes),
                    Summary = "Added subjects: " + string.Join(", ", newSubjects) + "; verbs: " + string.Join(", ", newVerbs) + "; story wishes: " + string.Join(" | ", newWishes.Select(x => x.SourceDisplayName + ": " + x.Wish)) + ".",
                    UpdatedAt = development.UpdatedAt
                });
                await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: " + termProfile.StarId
                    + " added " + newSubjects.Count.ToString(CultureInfo.InvariantCulture) + " subjects and "
                    + newVerbs.Count.ToString(CultureInfo.InvariantCulture) + " verbs; team pool is now "
                    + subjects.Count.ToString(CultureInfo.InvariantCulture) + " subjects / "
                    + verbs.Count.ToString(CultureInfo.InvariantCulture) + " verbs / "
                    + storyWishes.Count.ToString(CultureInfo.InvariantCulture) + " story wishes.");
            }

            // The viewpoint Star owns the initial architecture.
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: light seed pass complete with "
                + storyWishes.Count.ToString(CultureInfo.InvariantCulture) + " current story wishes. "
                + viewpointProfile.StarId + " is building the story architecture now.");
            StarWritingStoryOutlineResult outline = await _agent.CreateStoryOutlineAsync(
                viewpointProfile,
                state,
                subjects,
                verbs,
                storyWishes,
                _seedWordProvider?.CreateSeedWords() ?? Array.Empty<string>(),
                cancellationToken).ConfigureAwait(false);
            phaseNumber++;
            development.CurrentPhase = phaseNumber;
            development.Title = outline.Title;
            development.Premise = outline.Premise;
            development.Characters = (outline.Characters ?? new List<string>()).ToList();
            development.StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList();
            development.StoryWishes = CloneStoryWishes(storyWishes);
            development.OutlineBeats = outline.Beats.ToList();
            development.UpdatedAt = DateTimeOffset.UtcNow;
            development.Phases.Add(new StarWritingStoryDevelopmentPhase
            {
                PhaseNumber = phaseNumber,
                Stage = "outline",
                StarId = viewpointProfile.StarId,
                Characters = (outline.Characters ?? new List<string>()).ToList(),
                StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList(),
                StoryWishes = CloneStoryWishes(storyWishes),
                Summary = outline.Premise,
                UpdatedAt = development.UpdatedAt
            });
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: outline ready — “" + outline.Title
                + "” with " + outline.Beats.Count.ToString(CultureInfo.InvariantCulture) + " structural beats, "
                + (outline.Characters ?? new List<string>()).Count.ToString(CultureInfo.InvariantCulture) + " cast entries, and "
                + (outline.StorySubjects ?? new List<string>()).Count.ToString(CultureInfo.InvariantCulture) + " story subjects. Collaborative drafting begins.");

            List<StarProfile> builders = ShuffleProfiles(allProfiles
                .Where(x => !string.Equals(x.StarId, viewpointProfile.StarId, StringComparison.OrdinalIgnoreCase)));
            if (builders.Count < 2)
            {
                throw new InvalidOperationException("Constellation Forge needs at least two active collaborator Stars other than the viewpoint Star so pending finish can be audited by someone different from the requester.");
            }

            List<string> buildContributors = new List<string>();
            string currentStory = string.Empty;
            bool pendingFinish = false;
            string finishRequestedByStarId = string.Empty;
            bool completionApproved = false;
            string lastBuilderId = string.Empty;
            int buildPhaseCount = 0;

            while (!completionApproved)
            {
                StarProfile contributor = SelectStoryBuilder(builders, buildPhaseCount, lastBuilderId, finishRequestedByStarId);
                bool wasPendingFinish = pendingFinish;
                string buildStage = wasPendingFinish ? "finish audit" : (string.IsNullOrWhiteSpace(currentStory) ? "first full draft" : "development pass");
                ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: phase "
                    + (buildPhaseCount + 1).ToString(CultureInfo.InvariantCulture) + " — " + contributor.StarId
                    + " is running the " + buildStage + ". Current draft: " + CountWords(currentStory).ToString(CultureInfo.InvariantCulture) + " words.");
                StarWritingStoryBuildResult build = await _agent.BuildStoryPhaseAsync(
                    contributor,
                    viewpointProfile,
                    state,
                    outline,
                    currentStory,
                    subjects,
                    verbs,
                    storyWishes,
                    _seedWordProvider?.CreateSeedWords() ?? Array.Empty<string>(),
                    buildContributors,
                    builders.Select(x => x.StarId).ToList(),
                    buildPhaseCount + 1,
                    wasPendingFinish,
                    finishRequestedByStarId,
                    cancellationToken).ConfigureAwait(false);

                currentStory = build.Story.Trim();
                outline.Title = string.IsNullOrWhiteSpace(build.Title) ? outline.Title : build.Title.Trim();
                if (build.Characters != null && build.Characters.Count > 0)
                {
                    outline.Characters = build.Characters.ToList();
                }
                if (build.StorySubjects != null && build.StorySubjects.Count > 0)
                {
                    outline.StorySubjects = build.StorySubjects.ToList();
                }
                if (build.RevisedBeats != null && build.RevisedBeats.Count > 0)
                {
                    outline.Beats = build.RevisedBeats.ToList();
                }
                if (!buildContributors.Contains(contributor.StarId, StringComparer.OrdinalIgnoreCase))
                {
                    buildContributors.Add(contributor.StarId);
                }
                buildPhaseCount++;
                lastBuilderId = contributor.StarId;

                string decision = StarWritingStoryCompletionDecisions.Normalize(build.CompletionDecision, wasPendingFinish);
                int distinctBuilderCount = buildContributors.Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int requiredBuilderCount = builders.Select(x => x.StarId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                if (wasPendingFinish)
                {
                    if (string.Equals(decision, StarWritingStoryCompletionDecisions.ApprovePendingFinish, StringComparison.OrdinalIgnoreCase))
                    {
                        completionApproved = true;
                        pendingFinish = false;
                    }
                    else
                    {
                        pendingFinish = false;
                        finishRequestedByStarId = string.Empty;
                    }
                }
                else if (string.Equals(decision, StarWritingStoryCompletionDecisions.RequestFinish, StringComparison.OrdinalIgnoreCase))
                {
                    pendingFinish = true;
                    finishRequestedByStarId = contributor.StarId;
                }

                // Once every active collaborator has contributed, make sure the team explicitly performs a
                // pending-finish audit even if nobody volunteered a finish request on the first complete rotation.
                if (!completionApproved
                    && !pendingFinish
                    && distinctBuilderCount >= requiredBuilderCount
                    && buildPhaseCount >= requiredBuilderCount)
                {
                    pendingFinish = true;
                    finishRequestedByStarId = "team-completion-check";
                }

                phaseNumber++;
                development.CurrentPhase = phaseNumber;
                development.Title = outline.Title;
                development.Characters = (outline.Characters ?? new List<string>()).ToList();
                development.StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList();
                development.OutlineBeats = outline.Beats.ToList();
                development.CurrentStory = currentStory;
                development.StoryWishes = CloneStoryWishes(storyWishes);
                development.ContributingStarIds = buildContributors.ToList();
                development.PendingFinish = pendingFinish;
                development.FinishRequestedByStarId = finishRequestedByStarId;
                development.UpdatedAt = DateTimeOffset.UtcNow;
                development.Phases.Add(new StarWritingStoryDevelopmentPhase
                {
                    PhaseNumber = phaseNumber,
                    Stage = wasPendingFinish ? "finish-audit" : "build",
                    StarId = contributor.StarId,
                    Characters = (outline.Characters ?? new List<string>()).ToList(),
                    StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList(),
                    StoryWishes = CloneStoryWishes(storyWishes),
                    PendingFinish = wasPendingFinish,
                    CompletionDecision = decision,
                    Summary = string.Join("; ", build.AddedLayers ?? new List<string>()) + (string.IsNullOrWhiteSpace(build.CompletionReason) ? string.Empty : " Completion: " + build.CompletionReason),
                    StorySnapshot = currentStory,
                    UpdatedAt = development.UpdatedAt
                });
                state.LatestSelectedPassCount = Math.Max(1, buildPhaseCount);
                state.LatestCompletedPassCount = buildPhaseCount;
                state.LatestAppliedImprovementCount = buildPhaseCount;
                state.LatestPassUpdatedAt = development.UpdatedAt;
                await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
                ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: phase "
                    + buildPhaseCount.ToString(CultureInfo.InvariantCulture) + " complete — " + contributor.StarId
                    + " returned " + CountWords(currentStory).ToString(CultureInfo.InvariantCulture) + " words; cast="
                    + (outline.Characters ?? new List<string>()).Count.ToString(CultureInfo.InvariantCulture) + "; story subjects="
                    + (outline.StorySubjects ?? new List<string>()).Count.ToString(CultureInfo.InvariantCulture) + "; decision=" + decision
                    + (pendingFinish ? ". A different Star will audit the pending finish next." : "."));
            }

            // The building loop has no arbitrary phase ceiling. It ends only when one Star requests
            // completion and a different Star explicitly approves that pending finish.
            List<string> allContributors = new[] { viewpointProfile.StarId }
                .Concat(lightPassProfiles.Select(x => x.StarId))
                .Concat(buildContributors)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Return the completed collaborative draft to the viewpoint Star for the final authorial pass.
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: collaborative build approved after "
                + buildPhaseCount.ToString(CultureInfo.InvariantCulture) + " phases. Returning the complete draft to "
                + viewpointProfile.StarId + " for the final authorial revision.");
            StarWritingStoryFinalResult final = await _agent.FinalizeStoryAsync(
                viewpointProfile,
                state,
                outline,
                currentStory,
                subjects,
                verbs,
                storyWishes,
                allContributors,
                _seedWordProvider?.CreateSeedWords() ?? Array.Empty<string>(),
                cancellationToken).ConfigureAwait(false);
            phaseNumber++;
            development.CurrentPhase = phaseNumber;
            development.Title = final.Title;
            development.Characters = (outline.Characters ?? new List<string>()).ToList();
            development.StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList();
            development.CurrentStory = final.Story;
            development.StoryWishes = CloneStoryWishes(storyWishes);
            development.ContributingStarIds = allContributors.ToList();
            development.PendingFinish = false;
            development.FinishRequestedByStarId = string.Empty;
            development.Completed = true;
            development.FinalStep = final.FinalStep;
            development.UpdatedAt = DateTimeOffset.UtcNow;
            development.Phases.Add(new StarWritingStoryDevelopmentPhase
            {
                PhaseNumber = phaseNumber,
                Stage = "final-revision",
                StarId = viewpointProfile.StarId,
                Characters = (outline.Characters ?? new List<string>()).ToList(),
                StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList(),
                StoryWishes = CloneStoryWishes(storyWishes),
                CompletionDecision = "final",
                Summary = "Viewpoint Star completed the final funny/beautiful/deep/interesting revision with finalStep=true.",
                StorySnapshot = final.Story,
                UpdatedAt = development.UpdatedAt
            });
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: final revision complete — “" + final.Title
                + "”, " + CountWords(final.Story).ToString(CultureInfo.InvariantCulture) + " words. Running the compact reader-emotion IU vector review now.");

            StarWritingRating rating = await _agent.RateStoryAsync(
                viewpointProfile,
                state,
                final.Title,
                final.Story,
                allContributors,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    StarWritingReviewDisciplines.Normalize(rating.ReviewDiscipline),
                    StarWritingReviewDisciplines.ReaderEmotionIuV1,
                    StringComparison.OrdinalIgnoreCase)
                || !StarWritingReaderEmotionVector.TryNormalizeComplete(
                    rating.EmotionalVector,
                    out string normalizedEmotionVector,
                    out Dictionary<string, StarWritingEmotionValue> emotionValues))
            {
                throw new InvalidOperationException("Constellation Forge story review returned an incomplete compact ReaderEmotionIuV1 vector.");
            }
            rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
            rating.EmotionalVector = normalizedEmotionVector;
            rating.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
            rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(emotionValues, "Overall");
            List<string> matchedRules = GetMatchedConstellationRuleIds(viewpointProfile, emotionValues);
            string strongestEffects = string.Join(", ", StarWritingReaderEmotionVector.EmotionCategories
                .Select(emotion => new { Emotion = emotion, Value = StarWritingReaderEmotionVector.ReadValue(emotionValues, emotion) })
                .OrderByDescending(x => x.Value)
                .Take(4)
                .Select(x => x.Emotion + " " + x.Value.ToString("0.#", CultureInfo.InvariantCulture) + " IU"));
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: reader-emotion vector review returned Overall "
                + rating.OverallValue.ToString("0.0", CultureInfo.InvariantCulture) + " IU; strongest effects="
                + strongestEffects + "; persistence matches="
                + (matchedRules.Count > 0 ? string.Join(", ", matchedRules) : "none") + ". Saving the completed development trace and shelf result.");
            phaseNumber++;
            development.CurrentPhase = phaseNumber;
            development.UpdatedAt = DateTimeOffset.UtcNow;
            development.Phases.Add(new StarWritingStoryDevelopmentPhase
            {
                PhaseNumber = phaseNumber,
                Stage = "review",
                StarId = viewpointProfile.StarId,
                CompletionDecision = matchedRules.Count > 0 ? "persist" : "reject",
                Summary = matchedRules.Count > 0
                    ? "Passed Constellation reader-emotion persistence rules: " + string.Join(", ", matchedRules) + "."
                    : "Did not pass the Constellation reader-emotion persistence rules.",
                UpdatedAt = development.UpdatedAt
            });

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string batchId = "writing-batch-" + Guid.NewGuid().ToString("N");
            string itemId = "writing-" + Guid.NewGuid().ToString("N");
            StarWritingHistoryItem historyItem = new StarWritingHistoryItem
            {
                Id = itemId,
                WritingMethod = StarWritingMethods.ConstellationForge,
                OutputType = StarWritingOutputTypes.ConstellationStory,
                Title = final.Title,
                ContributingStarIds = allContributors.ToList(),
                StoryCharacters = (outline.Characters ?? new List<string>()).ToList(),
                StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList(),
                StoryWishes = CloneStoryWishes(storyWishes),
                SourceBatchId = batchId,
                GeneratedAt = now,
                Index = 0,
                WordsUsed = new List<string>(),
                Writing = final.Story,
                ReviewDiscipline = rating.ReviewDiscipline,
                EmotionalVector = rating.EmotionalVector,
                EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                OverallValue = rating.OverallValue,
                Persisted = matchedRules.Count > 0,
                MatchedPersistenceRuleIds = matchedRules.ToList()
            };
            StarWritingHistoryBatch batch = new StarWritingHistoryBatch
            {
                Id = batchId,
                WritingMethod = StarWritingMethods.ConstellationForge,
                OutputType = StarWritingOutputTypes.ConstellationStory,
                GeneratedAt = now,
                SelectedPassCount = development.Phases.Count,
                CompletedPassCount = development.Phases.Count,
                AppliedImprovementCount = buildPhaseCount,
                Items = new List<StarWritingHistoryItem> { historyItem }
            };

            int persistedCount = 0;
            if (historyItem.Persisted)
            {
                state.PersistedWritings.Add(new PersistedStarWriting
                {
                    Id = itemId,
                    WritingMethod = StarWritingMethods.ConstellationForge,
                    OutputType = StarWritingOutputTypes.ConstellationStory,
                    Title = final.Title,
                    ContributingStarIds = allContributors.ToList(),
                    StoryCharacters = (outline.Characters ?? new List<string>()).ToList(),
                    StorySubjects = (outline.StorySubjects ?? new List<string>()).ToList(),
                    StoryWishes = CloneStoryWishes(storyWishes),
                    SourceBatchId = batchId,
                    WordsUsed = new List<string>(),
                    Writing = final.Story,
                    ReviewDiscipline = rating.ReviewDiscipline,
                    EmotionalVector = rating.EmotionalVector,
                    EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                    OverallValue = rating.OverallValue,
                    MatchedPersistenceRuleIds = matchedRules.ToList(),
                    GeneratedAt = now,
                    PersistedAt = now,
                    ToldCount = 0
                });
                persistedCount = 1;
            }

            state.RecentWritingHistory.Add(historyItem);
            state.RecentWritingHistory = state.RecentWritingHistory
                .Where(x => x != null)
                .OrderByDescending(x => x.GeneratedAt)
                .Take(10)
                .OrderBy(x => x.GeneratedAt)
                .ThenBy(x => x.Index)
                .ToList();
            state.LatestWritingMethod = StarWritingMethods.ConstellationForge;
            state.LatestSelectedPassCount = development.Phases.Count;
            state.LatestCompletedPassCount = development.Phases.Count;
            state.LatestAppliedImprovementCount = buildPhaseCount;
            state.LatestPassUpdatedAt = now;
            RebuildUsedWordsFromPersistedWritings(state);
            await _store.SaveGeneratedBatchAsync(state, viewpointProfile.DisplayName, batch, CancellationToken.None).ConfigureAwait(false);
            ReportProgress(progress, viewpointProfile.StarId + " Constellation Forge: complete — “" + final.Title + "” "
                + (persistedCount > 0 ? "was persisted to the shelf." : "was reviewed but did not meet the current persistence rules."));

            return new StarWritingCreateResult
            {
                WritingMethod = StarWritingMethods.ConstellationForge,
                OutputType = StarWritingOutputTypes.ConstellationStory,
                State = state,
                Batch = batch,
                PersistedCount = persistedCount,
                RepairedRatingCount = repairedRatingCount,
                SelectedPassCount = development.Phases.Count,
                CompletedPassCount = development.Phases.Count,
                AppliedImprovementCount = buildPhaseCount
            };
        }

        public Task<StarWritingPerformanceResult?> TellPendingAsync(string starId, CancellationToken cancellationToken)
        {
            return TellPendingAsync(starId, StarWritingOutputTypes.Spark, cancellationToken);
        }

        public async Task<StarWritingPerformanceResult?> TellPendingAsync(string starId, string outputType, CancellationToken cancellationToken)
        {
            string normalizedOutputType = StarWritingOutputTypes.Normalize(outputType);

            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StarProfile profile = _profiles.GetProfile(starId);
                StarWritingNotesState state = await _store.LoadAsync(profile.StarId, cancellationToken).ConfigureAwait(false);
                if (string.Equals(normalizedOutputType, StarWritingOutputTypes.ConstellationStory, StringComparison.OrdinalIgnoreCase))
                {
                    return await PrepareConstellationStoryPerformanceAsync(profile, state, cancellationToken).ConfigureAwait(false);
                }

                return await PrepareSparkPerformanceAsync(profile, state, cancellationToken).ConfigureAwait(false);
            }
            finally { _operationGate.Release(); }
        }

        private async Task<StarWritingPerformanceResult?> PrepareSparkPerformanceAsync(
            StarProfile profile,
            StarWritingNotesState state,
            CancellationToken cancellationToken)
        {
            List<PersistedStarWriting> pending = state.PersistedWritings
                .Where(x => x != null
                    && x.ToldCount <= 0
                    && !string.IsNullOrWhiteSpace(x.Id)
                    && !string.IsNullOrWhiteSpace(x.Writing)
                    && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.Spark, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pending.Count == 0) return null;

            // Build a compact listening set from the strongest 100 untold Sparks. The top few are
            // always present as quality anchors, most other slots are IU-weighted random picks, and
            // a small lower band of the elite pool gives the Star enough contrast to recognize peaks.
            List<PersistedStarWriting> elitePool = StarWritingDeterministicRanker.TakeHighestRanked(
                pending,
                SparkPerformanceElitePoolLimit);
            SparkPerformanceCandidateSet candidateSet = BuildSparkPerformanceCandidateSet(elitePool);
            List<PersistedStarWriting> performanceCandidates = candidateSet.Items;

            StarWritingPerformancePlan plan = await _agent.PrepareWritingPerformanceAsync(
                profile,
                state,
                performanceCandidates,
                cancellationToken).ConfigureAwait(false);

            HashSet<string> candidateIds = new HashSet<string>(performanceCandidates.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            int targetCount = Math.Min(5, performanceCandidates.Count);
            List<string> requestedIds = (plan.SelectedWritingIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            List<string> invalidIds = requestedIds
                .Where(id => !candidateIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> selectedIds = requestedIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (invalidIds.Count > 0 || selectedIds.Count != targetCount || requestedIds.Count != selectedIds.Count)
            {
                throw new InvalidOperationException(
                    profile.StarId + " Spark performance selection was invalid. Expected "
                    + targetCount.ToString(CultureInfo.InvariantCulture)
                    + " distinct IDs from the weighted IU listening set, received "
                    + requestedIds.Count.ToString(CultureInfo.InvariantCulture)
                    + " entries / " + selectedIds.Count.ToString(CultureInfo.InvariantCulture)
                    + " distinct; invalid IDs=" + (invalidIds.Count == 0 ? "none" : string.Join(",", invalidIds))
                    + ". Raw model response: " + DiagnosticPreview(plan.RawModelJson));
            }

            List<PersistedStarWriting> selected = selectedIds
                .Select(id => performanceCandidates.First(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            List<string> spoken = (plan.SpokenText ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (spoken.Count != selected.Count)
            {
                throw new InvalidOperationException(
                    profile.StarId + " Spark performance returned "
                    + spoken.Count.ToString(CultureInfo.InvariantCulture)
                    + " spoken parts for " + selected.Count.ToString(CultureInfo.InvariantCulture)
                    + " selected Sparks. Raw model response: " + DiagnosticPreview(plan.RawModelJson));
            }

            DateTimeOffset toldAt = DateTimeOffset.UtcNow;
            foreach (PersistedStarWriting item in selected)
            {
                item.ToldCount++;
                item.LastToldAt = toldAt;
            }
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                EmotionalPurpose = "share_favorite_high_value_sparks",
                VoiceDirective = CreateVoiceDirective(profile, selected)
            };
            response.SetSpokenParts(spoken);

            return new StarWritingPerformanceResult
            {
                OutputType = StarWritingOutputTypes.Spark,
                State = state,
                SelectedWritings = selected,
                CandidateCount = performanceCandidates.Count,
                ElitePoolCount = candidateSet.ElitePoolCount,
                AnchorCount = candidateSet.AnchorCount,
                ContrastCount = candidateSet.ContrastCount,
                Response = response
            };
        }

        private async Task<StarWritingPerformanceResult?> PrepareConstellationStoryPerformanceAsync(
            StarProfile profile,
            StarWritingNotesState state,
            CancellationToken cancellationToken)
        {
            List<PersistedStarWriting> pendingStories = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                .Where(x => x != null
                    && x.ToldCount <= 0
                    && !string.IsNullOrWhiteSpace(x.Id)
                    && !string.IsNullOrWhiteSpace(x.Writing)
                    && string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.ConstellationStory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (pendingStories.Count == 0) return null;

            // Story choice itself does not need another LLM context window. Pick the highest Overall IU
            // unplayed story deterministically. Public IU and stable metadata only break exact ties.
            PersistedStarWriting selected = pendingStories
                .OrderByDescending(StarWritingDeterministicRanker.OverallValue)
                .ThenByDescending(StarWritingDeterministicRanker.PublicSharingValue)
                .ThenByDescending(x => x.PersistedAt)
                .ThenBy(x => x.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .First();
            double overallValue = StarWritingDeterministicRanker.OverallValue(selected);
            double publicValue = StarWritingDeterministicRanker.PublicSharingValue(selected);

            // Only the selected story is sent in full. The LLM's job here is not to choose or rewrite it;
            // it simply turns the complete stored prose into the ordered spokenText array used by the
            // existing phone TTS queue.
            StarWritingPerformancePlan plan = await _agent.PrepareConstellationStoryPerformanceAsync(
                profile,
                state,
                selected,
                cancellationToken).ConfigureAwait(false);

            List<string> selectedIds = (plan.SelectedWritingIds ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selectedIds.Count != 1 || !string.Equals(selectedIds[0], selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    profile.StarId + " Constellation Story playback formatter returned the wrong story ID. Expected "
                    + selected.Id + ", received " + (selectedIds.Count == 0 ? "none" : string.Join(",", selectedIds))
                    + ". Raw model response: " + DiagnosticPreview(plan.RawModelJson));
            }

            List<string> spoken = (plan.SpokenText ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();
            if (spoken.Count == 0)
            {
                throw new InvalidOperationException(
                    profile.StarId + " Constellation Story playback formatter returned no spokenText chunks for “"
                    + (string.IsNullOrWhiteSpace(selected.Title) ? selected.Id : selected.Title)
                    + "”. Raw model response: " + DiagnosticPreview(plan.RawModelJson));
            }

            selected.ToldCount++;
            selected.LastToldAt = DateTimeOffset.UtcNow;
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            StarInteractionResponse response = new StarInteractionResponse
            {
                StarId = profile.StarId,
                StoryId = selected.Id,
                StoryTitle = string.IsNullOrWhiteSpace(selected.Title) ? "Constellation Story" : selected.Title.Trim(),
                StoryIsFinished = true,
                StoryPartIndex = 0,
                EmotionalPurpose = "narrate_highest_value_unplayed_constellation_story",
                VoiceDirective = CreateConstellationStoryVoiceDirective(profile, selected)
            };
            response.SetSpokenParts(spoken);

            return new StarWritingPerformanceResult
            {
                OutputType = StarWritingOutputTypes.ConstellationStory,
                State = state,
                SelectedWritings = new List<PersistedStarWriting> { selected },
                SelectedOverallValue = overallValue,
                SelectedPublicValue = publicValue,
                CandidateCount = 1,
                ElitePoolCount = pendingStories.Count,
                Response = response
            };
        }

        private SparkPerformanceCandidateSet BuildSparkPerformanceCandidateSet(IReadOnlyList<PersistedStarWriting> rankedElitePool)
        {
            List<PersistedStarWriting> elite = (rankedElitePool ?? Array.Empty<PersistedStarWriting>())
                .Where(x => x != null)
                .ToList();
            int targetCount = Math.Min(SparkPerformanceCandidateLimit, elite.Count);
            if (targetCount <= 0)
            {
                return new SparkPerformanceCandidateSet();
            }

            // If the elite pool itself is already small enough, keep all of it. Shuffling still avoids
            // teaching the model that the first item is necessarily the best item.
            if (elite.Count <= targetCount)
            {
                List<PersistedStarWriting> all = elite.ToList();
                ShuffleInPlace(all);
                return new SparkPerformanceCandidateSet
                {
                    Items = all,
                    ElitePoolCount = elite.Count,
                    AnchorCount = Math.Min(SparkPerformanceTopAnchorCount, all.Count),
                    ContrastCount = 0
                };
            }

            int anchorCount = Math.Min(SparkPerformanceTopAnchorCount, targetCount);
            int contrastCount = Math.Min(SparkPerformanceContrastCount, Math.Max(0, targetCount - anchorCount));
            int contrastStartIndex = Math.Max(
                anchorCount,
                Math.Min(elite.Count, (int)Math.Floor(elite.Count * SparkPerformanceContrastBandStart)));

            List<PersistedStarWriting> selected = elite.Take(anchorCount).ToList();
            HashSet<string> selectedIds = new HashSet<string>(
                selected.Where(x => !string.IsNullOrWhiteSpace(x.Id)).Select(x => x.Id),
                StringComparer.OrdinalIgnoreCase);

            List<PersistedStarWriting> upperPool = elite
                .Skip(anchorCount)
                .Take(Math.Max(0, contrastStartIndex - anchorCount))
                .Where(x => !selectedIds.Contains(x.Id))
                .ToList();
            List<PersistedStarWriting> contrastPool = elite
                .Skip(contrastStartIndex)
                .Where(x => !selectedIds.Contains(x.Id))
                .ToList();

            int upperTarget = Math.Max(0, targetCount - anchorCount - contrastCount);
            AddUnique(selected, selectedIds, WeightedSampleWithoutReplacement(upperPool, upperTarget));

            // These are intentionally sampled from the lower portion of the *top-100* pool, not from bad work.
            // They provide a little quality contrast so the truly exceptional Sparks are easier for the Star to
            // recognize instead of presenting twenty-five nearly indistinguishable peak-value candidates.
            List<PersistedStarWriting> contrastSelections = WeightedSampleWithoutReplacement(contrastPool, contrastCount);
            int countBeforeContrast = selected.Count;
            AddUnique(selected, selectedIds, contrastSelections);
            int actualContrastCount = selected.Count - countBeforeContrast;

            if (selected.Count < targetCount)
            {
                List<PersistedStarWriting> remaining = elite
                    .Where(x => !selectedIds.Contains(x.Id))
                    .ToList();
                AddUnique(selected, selectedIds, WeightedSampleWithoutReplacement(remaining, targetCount - selected.Count));
            }

            // Randomize presentation order. IU values remain attached to each Spark, so the Star can compare
            // quality from the content/value evidence rather than inheriting an ordering bias from the ranker.
            ShuffleInPlace(selected);
            return new SparkPerformanceCandidateSet
            {
                Items = selected.Take(targetCount).ToList(),
                ElitePoolCount = elite.Count,
                AnchorCount = anchorCount,
                ContrastCount = actualContrastCount
            };
        }

        private List<PersistedStarWriting> WeightedSampleWithoutReplacement(
            IReadOnlyList<PersistedStarWriting> source,
            int requestedCount)
        {
            List<PersistedStarWriting> pool = (source ?? Array.Empty<PersistedStarWriting>())
                .Where(x => x != null)
                .ToList();
            int take = Math.Min(Math.Max(0, requestedCount), pool.Count);
            List<PersistedStarWriting> result = new List<PersistedStarWriting>(take);

            while (result.Count < take && pool.Count > 0)
            {
                double minValue = pool.Min(x => StarWritingDeterministicRanker.PublicSharingValue(x));
                double maxValue = pool.Max(x => StarWritingDeterministicRanker.PublicSharingValue(x));
                double span = Math.Max(0.0001, maxValue - minValue);
                double totalWeight = 0.0;
                double[] weights = new double[pool.Count];

                for (int i = 0; i < pool.Count; i++)
                {
                    double value = StarWritingDeterministicRanker.PublicSharingValue(pool[i]);
                    double normalized = Math.Max(0.0, Math.Min(1.0, (value - minValue) / span));
                    // A curved 1x..10x weight keeps every item possible while giving the larger-IU works a
                    // materially better chance of entering the listening set.
                    double weight = 1.0 + (9.0 * normalized * normalized);
                    weights[i] = weight;
                    totalWeight += weight;
                }

                double roll = _random.NextDouble() * totalWeight;
                int chosenIndex = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    roll -= weights[i];
                    if (roll <= 0.0)
                    {
                        chosenIndex = i;
                        break;
                    }
                }

                result.Add(pool[chosenIndex]);
                pool.RemoveAt(chosenIndex);
            }

            return result;
        }

        private static void AddUnique(
            ICollection<PersistedStarWriting> destination,
            ISet<string> selectedIds,
            IEnumerable<PersistedStarWriting> additions)
        {
            foreach (PersistedStarWriting item in additions ?? Enumerable.Empty<PersistedStarWriting>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || !selectedIds.Add(item.Id)) continue;
                destination.Add(item);
            }
        }

        private void ShuffleInPlace(IList<PersistedStarWriting> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                PersistedStarWriting temp = items[i];
                items[i] = items[j];
                items[j] = temp;
            }
        }

        private sealed class SparkPerformanceCandidateSet
        {
            public List<PersistedStarWriting> Items { get; set; } = new List<PersistedStarWriting>();
            public int ElitePoolCount { get; set; }
            public int AnchorCount { get; set; }
            public int ContrastCount { get; set; }
        }

        public async Task ClearAsync(string starId, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await _store.ClearAsync(starId, cancellationToken).ConfigureAwait(false); }
            finally { _operationGate.Release(); }
        }

        private static string DiagnosticPreview(string? text, int maxChars = 1600)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length <= maxChars) return value;
            return value.Substring(0, Math.Max(0, maxChars)) + "…";
        }

        private static void ReportProgress(Action<string>? progress, string message)
        {
            if (progress == null || string.IsNullOrWhiteSpace(message)) return;
            try { progress(message.Trim()); }
            catch { /* UI progress is observational and must never break generation. */ }
        }

        private static int CountWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private List<StarProfile> ShuffleProfiles(IEnumerable<StarProfile> profiles)
        {
            List<StarProfile> values = (profiles ?? Enumerable.Empty<StarProfile>()).Where(x => x != null).ToList();
            for (int index = values.Count - 1; index > 0; index--)
            {
                int swapIndex = _random.Next(index + 1);
                (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
            }
            return values;
        }

        private static StarProfile SelectStoryBuilder(
            IReadOnlyList<StarProfile> builders,
            int buildPhaseCount,
            string lastBuilderId,
            string finishRequestedByStarId)
        {
            if (builders == null || builders.Count == 0) throw new InvalidOperationException("No collaborative story builders are available.");
            int start = Math.Abs(buildPhaseCount) % builders.Count;
            for (int offset = 0; offset < builders.Count; offset++)
            {
                StarProfile candidate = builders[(start + offset) % builders.Count];
                if (builders.Count > 1 && string.Equals(candidate.StarId, lastBuilderId, StringComparison.OrdinalIgnoreCase)) continue;
                if (builders.Count > 1 && !string.IsNullOrWhiteSpace(finishRequestedByStarId)
                    && !string.Equals(finishRequestedByStarId, "team-completion-check", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.StarId, finishRequestedByStarId, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate;
            }
            return builders[start];
        }

        private static List<StarWritingStoryWish> CloneStoryWishes(IEnumerable<StarWritingStoryWish>? wishes)
        {
            return (wishes ?? Enumerable.Empty<StarWritingStoryWish>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Wish))
                .Select(x => new StarWritingStoryWish
                {
                    SourceActorId = (x.SourceActorId ?? string.Empty).Trim(),
                    SourceDisplayName = (x.SourceDisplayName ?? string.Empty).Trim(),
                    SourceActorType = (x.SourceActorType ?? string.Empty).Trim(),
                    Wish = x.Wish.Trim()
                })
                .ToList();
        }

        private static List<string> AddUniqueTerms(List<string> destination, IEnumerable<string>? values)
        {
            List<string> added = new List<string>();
            HashSet<string> existing = new HashSet<string>(destination ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string value in values ?? Enumerable.Empty<string>())
            {
                string normalized = (value ?? string.Empty).Trim();
                if (normalized.Length == 0 || !existing.Add(normalized)) continue;
                destination.Add(normalized);
                added.Add(normalized);
            }
            return added;
        }

        private static void RecordStoryTerms(StarWritingNotesState state, IEnumerable<string>? nouns, IEnumerable<string>? verbs)
        {
            state.RecentStoryNouns ??= new List<string>();
            state.StoryNounUsageCounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            state.RecentStoryVerbs ??= new List<string>();
            state.StoryVerbUsageCounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            RecordRollingTerms(state.RecentStoryNouns, state.StoryNounUsageCounts, nouns, 50);
            RecordRollingTerms(state.RecentStoryVerbs, state.StoryVerbUsageCounts, verbs, 50);
        }

        private static void RecordRollingTerms(
            List<string> recent,
            Dictionary<string, int> counts,
            IEnumerable<string>? values,
            int recentLimit)
        {
            List<string> normalized = (values ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalized.Count == 0) return;

            recent.InsertRange(0, normalized);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            recent.RemoveAll(x => string.IsNullOrWhiteSpace(x) || !seen.Add(x.Trim()));
            if (recent.Count > recentLimit) recent.RemoveRange(recentLimit, recent.Count - recentLimit);

            foreach (string value in normalized)
            {
                counts[value] = counts.TryGetValue(value, out int current) ? current + 1 : 1;
            }
        }

        private static void RecordGeneratedWordSet(StarWritingNotesState state, IEnumerable<List<string>>? wordPairs)
        {
            List<List<string>> normalizedPairs = (wordPairs ?? Enumerable.Empty<List<string>>())
                .Where(pair => pair != null)
                .Select(pair => pair
                    .Where(word => !string.IsNullOrWhiteSpace(word))
                    .Select(word => word.Trim())
                    .Take(2)
                    .ToList())
                .Where(pair => pair.Count == 2)
                .Take(5)
                .ToList();
            if (normalizedPairs.Count == 0) return;

            state.RecentGeneratedWordSets ??= new List<StarWritingGeneratedWordSet>();
            state.RecentGeneratedWordSets.Add(new StarWritingGeneratedWordSet
            {
                GeneratedAt = DateTimeOffset.UtcNow,
                WordPairs = normalizedPairs.Select(pair => pair.ToList()).ToList()
            });
            state.RecentGeneratedWordSets = state.RecentGeneratedWordSets
                .Where(x => x != null)
                .OrderByDescending(x => x.GeneratedAt)
                .Take(10)
                .OrderBy(x => x.GeneratedAt)
                .ToList();

            Dictionary<string, int> usageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in state.GeneratedWordUsageCounts ?? new Dictionary<string, int>())
            {
                string word = (entry.Key ?? string.Empty).Trim();
                if (word.Length == 0 || entry.Value <= 0) continue;
                usageCounts[word] = usageCounts.TryGetValue(word, out int current)
                    ? current + entry.Value
                    : entry.Value;
            }
            foreach (string word in normalizedPairs.SelectMany(pair => pair))
            {
                usageCounts[word] = usageCounts.TryGetValue(word, out int current)
                    ? current + 1
                    : 1;
            }
            state.GeneratedWordUsageCounts = usageCounts;
        }

        private static void RecordGeneratedNouns(StarWritingNotesState state, IEnumerable<string>? nouns)
        {
            List<string> normalizedNouns = (nouns ?? Enumerable.Empty<string>())
                .Where(noun => !string.IsNullOrWhiteSpace(noun))
                .Select(noun => noun.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
            if (normalizedNouns.Count == 0) return;

            state.RecentGeneratedNouns ??= new List<string>();
            state.RecentGeneratedNouns = normalizedNouns
                .Concat(state.RecentGeneratedNouns)
                .Where(noun => !string.IsNullOrWhiteSpace(noun))
                .Select(noun => noun.Trim())
                .Take(50)
                .ToList();

            Dictionary<string, int> usageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in state.GeneratedNounUsageCounts ?? new Dictionary<string, int>())
            {
                string noun = (entry.Key ?? string.Empty).Trim();
                if (noun.Length == 0 || entry.Value <= 0) continue;
                usageCounts[noun] = usageCounts.TryGetValue(noun, out int current)
                    ? current + entry.Value
                    : entry.Value;
            }
            foreach (string noun in normalizedNouns)
            {
                usageCounts[noun] = usageCounts.TryGetValue(noun, out int current)
                    ? current + 1
                    : 1;
            }
            state.GeneratedNounUsageCounts = usageCounts;
        }

        private async Task SaveLatestPassStateAsync(StarWritingNotesState state, StarWritingGenerationResult generation)
        {
            state.LatestWritingMethod = StarWritingMethods.Normalize(generation.WritingMethod);
            state.LatestSelectedPassCount = generation.SelectedPassCount;
            state.LatestCompletedPassCount = generation.CompletedPassCount;
            state.LatestAppliedImprovementCount = generation.AppliedImprovementCount;
            state.LatestPassUpdatedAt = DateTimeOffset.UtcNow;
            state.LatestWritingPasses = ClonePasses(generation.Passes);
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }

        private static int ApplyImprovementReplacements(StarWritingGenerationResult generation, StarWritingImprovementResult improvement)
        {
            if (improvement?.Replacements == null || improvement.Replacements.Count == 0) return 0;
            bool displacementMethod = string.Equals(
                StarWritingMethods.Normalize(generation.WritingMethod),
                StarWritingMethods.VerbNounDisplacement,
                StringComparison.OrdinalIgnoreCase);
            Dictionary<string, StarWritingCandidate> candidates = generation.Items
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            HashSet<string> changedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (StarWritingImprovementReplacement replacement in improvement.Replacements.Where(x => x != null))
            {
                if (string.IsNullOrWhiteSpace(replacement.Id)
                    || !candidates.TryGetValue(replacement.Id.Trim(), out StarWritingCandidate candidate)
                    || !changedIds.Add(replacement.Id.Trim()))
                {
                    continue;
                }

                List<string> words = (replacement.NewWordSet ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string writing = (replacement.NewWriting ?? string.Empty).Trim();
                bool valuesAreValid = !double.IsNaN(replacement.OldValue)
                    && !double.IsInfinity(replacement.OldValue)
                    && !double.IsNaN(replacement.NewValue)
                    && !double.IsInfinity(replacement.NewValue)
                    && replacement.OldValue >= 0
                    && replacement.NewValue >= 0;
                if (words.Count != 2
                    || string.IsNullOrWhiteSpace(writing)
                    || !valuesAreValid
                    || replacement.NewValue <= replacement.OldValue)
                {
                    changedIds.Remove(replacement.Id.Trim());
                    continue;
                }

                if (displacementMethod)
                {
                    string proposedVerb = words[1];
                    bool duplicatesAnotherVerb = generation.Items
                        .Where(x => x != null && !ReferenceEquals(x, candidate))
                        .Select(x => x.WordsUsed != null && x.WordsUsed.Count > 1 ? x.WordsUsed[1] : string.Empty)
                        .Any(existingVerb => !string.IsNullOrWhiteSpace(existingVerb)
                            && string.Equals(existingVerb.Trim(), proposedVerb, StringComparison.OrdinalIgnoreCase));
                    if (duplicatesAnotherVerb)
                    {
                        changedIds.Remove(replacement.Id.Trim());
                        continue;
                    }
                }

                candidate.WordsUsed = words;
                candidate.Writing = writing;
            }

            generation.Jokes = generation.Items.Select(x => x.Writing).ToList();
            generation.UsedWords = generation.Items.Select(x => x.WordsUsed.ToList()).ToList();
            return changedIds.Count;
        }

        private static void AppendPassSnapshot(StarWritingGenerationResult generation, int passNumber, int appliedReplacementCount)
        {
            generation.Passes ??= new List<StarWritingPassSnapshot>();
            generation.Passes.Add(new StarWritingPassSnapshot
            {
                PassNumber = passNumber,
                AppliedReplacementCount = appliedReplacementCount,
                UpdatedAt = DateTimeOffset.UtcNow,
                Items = CloneCandidates(generation.Items)
            });
        }

        private static List<StarWritingPassSnapshot> ClonePasses(IEnumerable<StarWritingPassSnapshot>? passes)
        {
            return (passes ?? Enumerable.Empty<StarWritingPassSnapshot>())
                .Where(x => x != null)
                .Select(x => new StarWritingPassSnapshot
                {
                    PassNumber = x.PassNumber,
                    AppliedReplacementCount = x.AppliedReplacementCount,
                    UpdatedAt = x.UpdatedAt,
                    Items = CloneCandidates(x.Items)
                })
                .ToList();
        }

        private static List<StarWritingCandidate> CloneCandidates(IEnumerable<StarWritingCandidate>? items)
        {
            return (items ?? Enumerable.Empty<StarWritingCandidate>())
                .Where(x => x != null)
                .Select(x => new StarWritingCandidate
                {
                    Id = x.Id ?? string.Empty,
                    WordsUsed = (x.WordsUsed ?? new List<string>()).ToList(),
                    Writing = x.Writing ?? string.Empty
                })
                .ToList();
        }

        private static void NormalizeGeneration(StarWritingGenerationResult generation)
        {
            generation.WritingMethod = StarWritingMethods.Normalize(generation.WritingMethod);
            generation.OutputType = StarWritingOutputTypes.Normalize(string.IsNullOrWhiteSpace(generation.OutputType)
                ? StarWritingMethods.GetOutputType(generation.WritingMethod)
                : generation.OutputType);
            generation.Title = (generation.Title ?? string.Empty).Trim();
            generation.ContributingStarIds ??= new List<string>();
            generation.ContributingStarIds = generation.ContributingStarIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            generation.Jokes ??= new List<string>();
            generation.UsedWords ??= new List<List<string>>();
            generation.SeedWords ??= new List<string>();
            generation.Items ??= new List<StarWritingCandidate>();
            generation.Passes ??= new List<StarWritingPassSnapshot>();

            // The public response shape is two parallel arrays. Convert it into paired internal records.
            if (generation.Jokes.Count > 0 || generation.UsedWords.Count > 0)
            {
                generation.Items = Enumerable.Range(0, Math.Min(5, Math.Max(generation.Jokes.Count, generation.UsedWords.Count)))
                    .Select(index => new StarWritingCandidate
                    {
                        Writing = index < generation.Jokes.Count ? generation.Jokes[index] ?? string.Empty : string.Empty,
                        WordsUsed = index < generation.UsedWords.Count && generation.UsedWords[index] != null
                            ? generation.UsedWords[index].ToList()
                            : new List<string>()
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Writing))
                    .ToList();
            }
            else
            {
                generation.Items = generation.Items
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Writing))
                    .Take(5)
                    .ToList();
            }

            string[][] fallbacks =
            {
                new[] { "Starlight", "Pebble" }, new[] { "Void", "Teacup" }, new[] { "Shadow", "Tapestry" },
                new[] { "Fragment", "Echo" }, new[] { "Stone", "Whisper" }
            };
            while (generation.Items.Count < 5)
            {
                int index = generation.Items.Count;
                generation.Items.Add(new StarWritingCandidate
                {
                    WordsUsed = fallbacks[index].ToList(),
                    Writing = "The " + fallbacks[index][0].ToLowerInvariant() + " regarded the " + fallbacks[index][1].ToLowerInvariant() + " and decided their silence was already an answer."
                });
            }
            generation.Items = generation.Items.Take(5).ToList();
            for (int i = 0; i < generation.Items.Count; i++)
            {
                StarWritingCandidate item = generation.Items[i];
                item.Id = "candidate-" + (i + 1).ToString(CultureInfo.InvariantCulture);
                item.Writing = item.Writing.Trim();
                item.WordsUsed ??= new List<string>();
                item.WordsUsed = item.WordsUsed.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                while (item.WordsUsed.Count < 2) item.WordsUsed.Add(fallbacks[i][item.WordsUsed.Count]);
            }

            // Keep the externally visible arrays synchronized with the normalized one-to-one pairing.
            generation.Jokes = generation.Items.Select(x => x.Writing).ToList();
            generation.UsedWords = generation.Items.Select(x => x.WordsUsed.ToList()).ToList();
        }

        private async Task<int> RepairIncompleteRecentRatingsAsync(
            StarProfile profile,
            StarWritingNotesState state,
            CancellationToken cancellationToken)
        {
            List<StarWritingHistoryItem> invalid = (state.RecentWritingHistory ?? new List<StarWritingHistoryItem>())
                .Where(x => x != null
                    && NeedsRatingRepair(x)
                    && !string.Equals(StarWritingOutputTypes.Normalize(x.OutputType), StarWritingOutputTypes.ConstellationStory, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.GeneratedAt)
                .ThenBy(x => x.Index)
                .Take(10)
                .ToList();
            if (invalid.Count == 0) return 0;

            int repairedCount = 0;
            DateTimeOffset repairedAt = DateTimeOffset.UtcNow;
            for (int offset = 0; offset < invalid.Count; offset += 5)
            {
                List<StarWritingHistoryItem> group = invalid.Skip(offset).Take(5).ToList();
                StarWritingGenerationResult generation = new StarWritingGenerationResult
                {
                    StarId = profile.StarId,
                    WritingMethod = StarWritingMethods.Normalize(group.FirstOrDefault()?.WritingMethod),
                    Items = group.Select(item => new StarWritingCandidate
                    {
                        WordsUsed = (item.WordsUsed ?? new List<string>()).Take(2).ToList(),
                        Writing = item.Writing ?? string.Empty
                    }).ToList()
                };

                while (generation.Items.Count < 5)
                {
                    generation.Items.Add(new StarWritingCandidate
                    {
                        WordsUsed = new List<string> { "rating", "placeholder" },
                        Writing = "A temporary rating placeholder that will not be stored."
                    });
                }

                StarWritingReviewResult review = await _agent
                    .RateWritingCandidatesAsync(profile, state, generation, cancellationToken)
                    .ConfigureAwait(false);
                NormalizeReview(review);

                for (int i = 0; i < group.Count; i++)
                {
                    StarWritingHistoryItem item = group[i];
                    StarWritingRating rating = review.Ratings.First(x => x.Index == i);
                    if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                            rating.EmotionalVector,
                            out string repairedVector,
                            out Dictionary<string, StarWritingEmotionValue> values))
                    {
                        throw new InvalidOperationException("Spark rating repair returned an incomplete ReaderEmotionIuV1 vector.");
                    }
                    rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
                    rating.EmotionalVector = repairedVector;
                    rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
                    List<string> matchedRules = GetMatchedRuleIds(profile, values);

                    item.ReviewDiscipline = rating.ReviewDiscipline;
                    item.EmotionalVector = rating.EmotionalVector;
                    item.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                    item.OverallValue = rating.OverallValue;
                    item.Persisted = matchedRules.Count > 0;
                    item.MatchedPersistenceRuleIds = matchedRules;

                    PersistedStarWriting? existing = state.PersistedWritings
                        .FirstOrDefault(x => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
                    if (item.Persisted)
                    {
                        if (existing == null)
                        {
                            state.PersistedWritings.Add(new PersistedStarWriting
                            {
                                Id = item.Id,
                                WritingMethod = StarWritingMethods.Normalize(item.WritingMethod),
                                OutputType = StarWritingOutputTypes.Normalize(item.OutputType),
                                Title = item.Title ?? string.Empty,
                                ContributingStarIds = (item.ContributingStarIds ?? new List<string>()).ToList(),
                                SourceBatchId = item.SourceBatchId,
                                WordsUsed = (item.WordsUsed ?? new List<string>()).ToList(),
                                Writing = item.Writing,
                                ReviewDiscipline = item.ReviewDiscipline,
                                EmotionalVector = item.EmotionalVector,
                                EmotionFeedback = new List<StarWritingStoryEmotionValue>(),
                                OverallValue = item.OverallValue,
                                MatchedPersistenceRuleIds = matchedRules.ToList(),
                                GeneratedAt = item.GeneratedAt,
                                PersistedAt = repairedAt,
                                ToldCount = 0
                            });
                        }
                        else
                        {
                            existing.OutputType = StarWritingOutputTypes.Normalize(item.OutputType);
                            existing.Title = item.Title ?? string.Empty;
                            existing.ContributingStarIds = (item.ContributingStarIds ?? new List<string>()).ToList();
                            existing.SourceBatchId = item.SourceBatchId;
                            existing.WordsUsed = (item.WordsUsed ?? new List<string>()).ToList();
                            existing.Writing = item.Writing;
                            existing.ReviewDiscipline = item.ReviewDiscipline;
                            existing.EmotionalVector = item.EmotionalVector;
                            existing.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                            existing.OverallValue = item.OverallValue;
                            existing.MatchedPersistenceRuleIds = matchedRules.ToList();
                            existing.GeneratedAt = item.GeneratedAt;
                        }
                    }
                    else if (existing != null)
                    {
                        state.PersistedWritings.Remove(existing);
                    }

                    repairedCount++;
                }
            }

            if (repairedCount > 0)
            {
                RebuildUsedWordsFromPersistedWritings(state);
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return repairedCount;
        }

        private static bool NeedsRatingRepair(StarWritingHistoryItem item)
        {
            if (item == null) return true;
            string discipline = StarWritingReviewDisciplines.Normalize(item.ReviewDiscipline);
            if (string.Equals(discipline, StarWritingReviewDisciplines.ReaderEmotionIuV1, StringComparison.OrdinalIgnoreCase))
            {
                return !StarWritingReaderEmotionVector.TryNormalizeComplete(item.EmotionalVector, out _, out _);
            }
            return !StarWritingEmotionVector.TryNormalizeUsable(item.EmotionalVector, out _, out _);
        }

        private static void NormalizeReview(StarWritingReviewResult review)
        {
            review.Ratings ??= new List<StarWritingRating>();
            List<StarWritingRating> source = review.Ratings.Where(x => x != null).Take(5).ToList();
            if (source.Count != 5)
            {
                throw new InvalidOperationException("Writing review must contain exactly five ratings; received " + source.Count + ".");
            }

            List<StarWritingRating> normalized = new List<StarWritingRating>();
            HashSet<StarWritingRating> used = new HashSet<StarWritingRating>();
            for (int i = 0; i < 5; i++)
            {
                StarWritingRating? rating = source.FirstOrDefault(x => !used.Contains(x) && x.Index == i)
                    ?? source.FirstOrDefault(x => !used.Contains(x));
                if (rating == null)
                {
                    throw new InvalidOperationException("Writing review is missing rating index " + i + ".");
                }

                used.Add(rating);
                if (!StarWritingReaderEmotionVector.TryNormalizeComplete(
                    rating.EmotionalVector,
                    out string emotionalVector,
                    out Dictionary<string, StarWritingEmotionValue> values))
                {
                    throw new InvalidOperationException(
                        "Writing review rating " + i + " did not contain the complete 31-value ReaderEmotionIuV1 vector in category order.");
                }

                rating.Index = i;
                rating.ReviewDiscipline = StarWritingReviewDisciplines.ReaderEmotionIuV1;
                rating.EmotionalVector = emotionalVector;
                rating.EmotionFeedback = new List<StarWritingStoryEmotionValue>();
                rating.OverallValue = StarWritingReaderEmotionVector.ReadValue(values, "Overall");
                normalized.Add(rating);
            }

            review.Ratings = normalized;
        }

        private static void RebuildUsedWordsFromPersistedWritings(StarWritingNotesState state)
        {
            state.AllUsedWords = (state.PersistedWritings ?? new List<PersistedStarWriting>())
                .Where(x => x != null)
                .OrderBy(x => x.PersistedAt)
                .ThenBy(x => x.GeneratedAt)
                .SelectMany(x => x.WordsUsed ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetMatchedConstellationRuleIds(
            StarProfile profile,
            IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            bool passesProfileGate = PassesPersistenceQualityGate(profile.ConstellationPersistenceQualityGate, values);
            List<string> matched = new List<string>();

            List<StarWritingPersistenceRule> rules = profile.ConstellationPersistenceRules ?? new List<StarWritingPersistenceRule>();
            if (rules.Count == 0)
            {
                rules = new List<StarWritingPersistenceRule>
                {
                    new StarWritingPersistenceRule
                    {
                        Id = "constellation-default-overall",
                        Requirements = new List<StarWritingPersistenceRequirement>
                        {
                            new StarWritingPersistenceRequirement { Category = "Overall", MinimumValue = 75 }
                        }
                    }
                };
            }

            if (passesProfileGate)
            {
                matched.AddRange(MatchPersistenceRules(rules, values));
            }
            if (values.TryGetValue("Overall", out StarWritingEmotionValue? overall)
                && overall.Value > GlobalOverallValueThreshold
                && !matched.Contains(GlobalOverallRuleId, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(GlobalOverallRuleId);
            }
            return matched;
        }

        private static List<string> MatchPersistenceRules(
            IEnumerable<StarWritingPersistenceRule>? rules,
            IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            List<string> matched = new List<string>();
            foreach (StarWritingPersistenceRule rule in (rules ?? Enumerable.Empty<StarWritingPersistenceRule>())
                .Where(x => x != null && x.Requirements != null && x.Requirements.Count > 0))
            {
                IEnumerable<bool> checks = rule.Requirements.Select(requirement =>
                {
                    if (!values.TryGetValue(requirement.Category ?? string.Empty, out StarWritingEmotionValue? value)) return false;
                    return value.Value >= requirement.MinimumValue;
                });
                bool isAny = string.Equals(rule.MatchMode, "any", StringComparison.OrdinalIgnoreCase);
                bool qualifies = isAny ? checks.Any(x => x) : checks.All(x => x);
                if (qualifies) matched.Add(string.IsNullOrWhiteSpace(rule.Id) ? "writing-rule" : rule.Id.Trim());
            }
            return matched;
        }

        private static List<string> GetMatchedRuleIds(StarProfile profile, IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            // Keep the authored Spark gate and rule shapes intact. The only exception is the global
            // Overall excellence backstop, which is an independent integrated judgment and must not
            // disappear merely because an excellent tiny Spark has a concentrated emotional profile.
            bool passesProfileGate = PassesPersistenceQualityGate(profile.WritingPersistenceQualityGate, values);
            List<string> matched = new List<string>();

            List<StarWritingPersistenceRule> rules = profile.WritingPersistenceRules ?? new List<StarWritingPersistenceRule>();
            if (rules.Count == 0)
            {
                rules = new List<StarWritingPersistenceRule>
                {
                    new StarWritingPersistenceRule
                    {
                        Id = "default-overall",
                        Requirements = new List<StarWritingPersistenceRequirement>
                        {
                            new StarWritingPersistenceRequirement { Category = "Overall", MinimumValue = 75 }
                        }
                    }
                };
            }

            if (passesProfileGate)
            {
                matched.AddRange(MatchPersistenceRules(rules, values));
            }

            // Every Star also gets the same conservative excellence backstop. The comparison is
            // deliberately strict: an Overall value of exactly 80 does not qualify at the default
            // threshold, while 80.01 and above do. Character-specific rules remain the primary
            // route; this catches rare excellent pieces whose strengths do not fit their rule shape.
            if (values.TryGetValue("Overall", out StarWritingEmotionValue? overall)
                && overall.Value > GlobalOverallValueThreshold
                && !matched.Contains(GlobalOverallRuleId, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(GlobalOverallRuleId);
            }

            return matched;
        }

        private static bool PassesPersistenceQualityGate(
            StarWritingPersistenceQualityGate? gate,
            IReadOnlyDictionary<string, StarWritingEmotionValue> values)
        {
            if (gate == null) return true;

            if (gate.MinimumOverallValue > 0)
            {
                if (!values.TryGetValue("Overall", out StarWritingEmotionValue? overall)
                    || overall.Value < gate.MinimumOverallValue)
                {
                    return false;
                }
            }

            List<StarWritingPersistenceRequirement> supporting = (gate.SupportingRequirements
                    ?? new List<StarWritingPersistenceRequirement>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Category))
                .ToList();
            if (supporting.Count == 0) return true;

            int requiredMatches = gate.MinimumSupportingMatches <= 0
                ? supporting.Count
                : Math.Min(gate.MinimumSupportingMatches, supporting.Count);
            int matched = supporting.Count(requirement =>
                values.TryGetValue(requirement.Category, out StarWritingEmotionValue? value)
                && value.Value >= requirement.MinimumValue);
            return matched >= requiredMatches;
        }

        private static double ReadGlobalOverallValueThreshold()
        {
            string? configured = Environment.GetEnvironmentVariable("WDS_STAR_WRITING_GLOBAL_OVERALL_VALUE_THRESHOLD")
                ?? Environment.GetEnvironmentVariable("WDS_STAR_WRITING_GLOBAL_OVERALL_THRESHOLD");
            if (!string.IsNullOrWhiteSpace(configured)
                && double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return Math.Max(0.0, parsed);
            }

            return DefaultGlobalOverallValueThreshold;
        }

        private static IEnumerable<KeyValuePair<string, StarWritingEmotionValue>> ReadEmotionValues(PersistedStarWriting writing)
        {
            if (writing != null
                && string.Equals(
                    StarWritingReviewDisciplines.Normalize(writing.ReviewDiscipline),
                    StarWritingReviewDisciplines.ReaderEmotionIuV1,
                    StringComparison.OrdinalIgnoreCase)
                && StarWritingReaderEmotionVector.TryNormalizeComplete(writing.EmotionalVector, out _, out Dictionary<string, StarWritingEmotionValue> readerValues))
            {
                return readerValues;
            }

            StarWritingEmotionVector.Normalize(writing?.EmotionalVector, out Dictionary<string, StarWritingEmotionValue> values);
            return values;
        }

        private static VoiceDirective CreateConstellationStoryVoiceDirective(StarProfile profile, PersistedStarWriting story)
        {
            string primary = "Poetic";
            double intensity = 0.68;
            HashSet<string> speakableEmotions = new HashSet<string>(new[]
            {
                "Funny", "Deep", "Intrigued", "Interested", "Playful", "Surprising",
                "Mysterious", "Haunting", "Melancholy", "Hopeful", "Romantic", "Moving",
                "Mirthful", "Delighted", "Engrossed", "Wondrous", "Awed", "Moved", "Tender",
                "Comforted", "Inspired", "Impassioned", "Exhilarated", "Connected", "Attracted",
                "Yearning", "Vulnerable", "Haunted", "Uneasy", "Afraid", "Concerned", "Angry", "Determined", "Contemplative"
            }, StringComparer.OrdinalIgnoreCase);
            KeyValuePair<string, StarWritingEmotionValue> strongest = ReadEmotionValues(story)
                .Where(x => speakableEmotions.Contains(x.Key))
                .OrderByDescending(x => x.Value.Value)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(strongest.Key))
            {
                primary = strongest.Key;
                intensity = Math.Max(0.25, Math.Min(1, strongest.Value.Value / 120.0));
            }

            VoiceDirective directive = new VoiceDirective
            {
                VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId,
                VoiceDesignPrompt = string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt) ? profile.VoiceDirection : profile.VoiceDesignPrompt,
                VoiceCloneAudioFile = profile.VoiceCloneAudioFile,
                VoiceCloneRefText = profile.VoiceCloneRefText,
                DeliveryInstructions = "Narrate the complete stored Constellation Story in character as " + profile.DisplayName + ". Treat the supplied spokenText chunks as one continuous story: preserve continuity across chunks, give dialogue natural character, let humor breathe, and let action, worry, romance, beauty, horror, or tenderness land according to the prose. Do not add commentary between chunks.",
                PrimaryEmotion = primary,
                EmotionIntensity = intensity,
                Pace = "natural story pace, varied by scene",
                Pitch = "natural",
                Volume = "normal-soft",
                Prosody = "continuous expressive narration with clean breath boundaries",
                Language = "Auto"
            };
            directive.Avoid.Add("robotic list reading");
            directive.Avoid.Add("announcing chunk numbers");
            directive.Avoid.Add("adding commentary between story passages");
            directive.Avoid.Add("explaining shelf values or playback mechanics");
            return directive;
        }

        private static VoiceDirective CreateVoiceDirective(StarProfile profile, IReadOnlyList<PersistedStarWriting> selected)
        {
            string primary = "Poetic";
            double intensity = 0.65;
            if (selected.Count > 0)
            {
                HashSet<string> speakableEmotions = new HashSet<string>(new[]
                {
                    "Funny", "Deep", "Intrigued", "Interested", "Playful", "Surprising",
                    "Mysterious", "Haunting", "Melancholy", "Hopeful", "Romantic", "Moving"
                }, StringComparer.OrdinalIgnoreCase);
                var strongest = selected
                    .SelectMany(ReadEmotionValues)
                    .Where(x => speakableEmotions.Contains(x.Key))
                    .OrderByDescending(x => x.Value.Value)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(strongest.Key))
                {
                    primary = strongest.Key;
                    intensity = Math.Max(0.25, Math.Min(1, strongest.Value.Value / 100.0));
                }
            }
            VoiceDirective directive = new VoiceDirective
            {
                VoiceId = string.IsNullOrWhiteSpace(profile.VoiceId) ? profile.StarId : profile.VoiceId,
                VoiceDesignPrompt = string.IsNullOrWhiteSpace(profile.VoiceDesignPrompt) ? profile.VoiceDirection : profile.VoiceDesignPrompt,
                VoiceCloneAudioFile = profile.VoiceCloneAudioFile,
                VoiceCloneRefText = profile.VoiceCloneRefText,
                DeliveryInstructions = "Speak in character as " + profile.DisplayName + ". Present each chosen joke or poetic fragment as something personally composed and worth sharing. Preserve timing, pauses, wit, and emotional restraint.",
                PrimaryEmotion = primary,
                EmotionIntensity = intensity,
                Pace = "slow-medium, with deliberate comic and poetic timing",
                Pitch = "natural",
                Volume = "normal-soft",
                Prosody = "intimate, clear, expressive, never rushed",
                Language = "Auto"
            };
            directive.Avoid.Add("robotic list reading");
            directive.Avoid.Add("breaking character");
            directive.Avoid.Add("explaining the scoring vector aloud");
            return directive;
        }
    }
}
