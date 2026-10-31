using System.Text.Json;
using Wds.SixStars.Api.Ai;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Engine;
using Wds.SixStars.Api.State;
using Wds.SixStars.Api.Stars;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static GirlfriendVectorItem Item(string id, double score, double confidence = 1) => new() { ItemId = id, DisplayName = id, Score = score, Confidence = confidence, Reason = "He listened when she asked for comfort." };
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
jsonOptions.Converters.Add(new FlexibleListJsonConverterFactory());
var model = JsonSerializer.Deserialize<StarStructuredModelResponse>("""{"boyfriendPerformanceVector":["respect^Respect^0.9^95^He honored her request for space."]}""", jsonOptions)!;
Check(model.BoyfriendPerformanceVector.Single().Score == 95, "Compact AI grade vector decoding");
var schema = JsonSerializer.SerializeToElement(StarResponseJsonSchema.CreateBackgroundReviewRawSchema());
Check(schema.GetProperty("properties").TryGetProperty("boyfriendPerformanceVector", out _) && schema.GetProperty("required").EnumerateArray().Any(x => x.GetString()=="boyfriendPerformanceVector"), "Strict AI schema must permit and request grade evidence");
var now = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
var state = new StarState { StarId = "Elusiaa", IsGirlfriend = true };
var ledger = state.BoyfriendGradeLedger = new();
void Record(string id, DateTimeOffset at, double score, double confidence = 1) => BoyfriendGrades.Record(ledger, state, id, at, Array.Empty<GirlfriendVectorItem>(), Array.Empty<GirlfriendVectorItem>(), "", new[] { Item("comfort", score, confidence) });
Check(BoyfriendGrades.Build(state, now).Categories.All(x => x.Grade == "Not yet rated"), "Empty state must not fail or invent grades");
Record("old1", now.AddDays(-8), 100); Record("old2", now.AddDays(-8).AddMinutes(1), 100); Record("old3", now.AddDays(-8).AddMinutes(2), 100);
Record("week1", now.AddHours(-3), 80); Record("week2", now.AddHours(-2), 80); Record("week3", now.AddHours(-1), 80);
var category = BoyfriendGrades.Build(state, now).Categories[1];
Check(category.Score == 90 && category.WeekScore == 80 && category.Grade == "A" && category.WeekGrade == "B", "Cumulative and calendar-week isolation");
Check(BoyfriendGrades.Build(state, now).WeekStartsAt == new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), "Monday UTC boundary");
Record("week3", now, 0); Record("uncertain", now, 0, .1);
Check(BoyfriendGrades.Build(state, now).Categories[1].EvidenceCount == 6, "Replay and low-confidence evidence must not count");
var saved = JsonSerializer.Deserialize<StarState>(JsonSerializer.Serialize(state))!;
saved.GirlfriendNeedsMetHistory.Clear(); saved.GirlfriendDesiredStateHistory.Clear();
Check(BoyfriendGrades.Build(saved, now).Categories[1].Score == 90, "Reload/pruned memories must preserve cumulative grade");
Check(BoyfriendGrades.Build(state, now.AddDays(7)).Categories[1].WeekGrade == "Not yet rated", "New week must reset only weekly view");
Check(BoyfriendGrades.Build(state, now.AddDays(7)).Categories[1].Score == 90, "Offline time must not penalize cumulative grade");
state.GirlfriendDesiredStateHistory.Add(new() { CreatedAt = now.AddMinutes(-30), NeedVector = new() { Item("comfort",100) } });
Record("important", now, 100);
Check(ledger.Totals[0].Weight == 8.5, "High priority need gets 2.5x weight");
Record("dismissal", now.AddSeconds(1), 0);
Check(BoyfriendGrades.Build(state, now.AddSeconds(1)).Categories[1].Score < 90, "Explicit poor response lowers grade");
var future = new StarState { IsGirlfriend = true, GirlfriendSince = now };
var futureLedger = new BoyfriendGradeLedger();
BoyfriendGrades.Record(futureLedger, future, "pre", now.AddDays(-1), new[] { Item("comfort",100) }, Array.Empty<GirlfriendVectorItem>(), "");
Check(futureLedger.Totals.Count == 0, "Pre-relationship interactions excluded");
var legacy = new StarState { IsGirlfriend = true };
legacy.GirlfriendCoupleActivityHistory.Add(new() { Id = "hers", CreatedAt = now, ActivityVector = new() { Item("hugging",100) } });
Check(BoyfriendGrades.Build(legacy, now).Categories.All(x => x.EvidenceCount == 0), "Her actions alone cannot earn his grade");
for (int i=0; i<3; i++) legacy.GirlfriendNeedsMetHistory.Add(new() { Id = "met"+i, CreatedAt=now.AddHours(-i), MetNeedVector=new() { Item("affection",20) } });
Check(BoyfriendGrades.Build(legacy, now).Categories[0].Grade == "B", "Small welcome affection is good, not a failure");
Console.WriteLine("PASS grade boundaries, priority weighting, deduplication, persistence, offline time, historical import and attribution.");

var store = new InMemoryStarStateStore();
var persisted = await store.GetAsync("test", "Elusiaa", default); persisted.IsGirlfriend=true;
var memory = new StarMemoryApi(store);
var turn = DateTimeOffset.UtcNow;
await memory.UpdateStarWithInteractionAsync(new() { StarId="Elusiaa", TurnStartedAt=turn }, default);
var update = new StarStateUpdateRequest { StarId="Elusiaa", TurnStartedAt=turn, BoyfriendPerformanceVector=new() { Item("comfort",90) } };
await memory.UpdateStarWithInteractionAsync(update, default);
await memory.UpdateStarWithInteractionAsync(update, default);
Check(persisted.BoyfriendGradeLedger!.Totals.Single().EvidenceCount == 1, "Empty foreground must not consume later review; repeat review must not double count");
Console.WriteLine("PASS actual memory API foreground/review and replay integration.");

var proposalStore = new InMemoryStarStateStore();
var ready = await proposalStore.GetAsync("test", "Elusiaa", default); ready.IsAlly = true; ready.IsGirlfriendReady = true;
var agent = new EmptyDecisionAgent();
var runtime = new SixStarsRuntime(new DefaultStarProfileProvider(), agent, new RelationshipDeltaCalculator(), proposalStore);
var done = new TaskCompletionSource<StarInteractionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
runtime.GirlfriendProposalCompleted += (_, answer) => done.TrySetResult(answer);
runtime.GirlfriendProposalFailed += (_, error) => done.TrySetException(error);
var request = new StarInteractionRequest { SessionId="test", StarId="Elusiaa", RequestType=RequestType.Chat, PlayerText="Will you be my girlfriend?" };
request.GameContext.Flags["girlfriendProposalDetected"]="true";
using var foregroundToken = new CancellationTokenSource();
var thinking = await runtime.ProcessAsync(request, foregroundToken.Token);
await agent.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
Check(!done.Task.IsCompleted, "Answer must wait for its thinking phrase transport");
foregroundToken.Cancel();
runtime.CancelBackgroundAnalysis("Simulated player activity while the proposal is waiting for acknowledgement");
runtime.CompleteGirlfriendProposalThinking(thinking);
var answer = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
Check(!string.IsNullOrWhiteSpace(answer.SpokenText) && answer.SpokenText.Contains("Yes"), "Empty generated speech needs an audible acceptance");
Check((await proposalStore.GetAsync("test", "Elusiaa", default)).IsGirlfriend, "Answer matches persisted relationship");
Check(answer.UnityEvents.Any(x => x.Type=="GirlfriendProposalCompleted"), "Follow-up event for client conversation");
Console.WriteLine("PASS real runtime proposal ordering, nonempty fallback and persisted acceptance.");

sealed class EmptyDecisionAgent : IStarAgent
{
    public TaskCompletionSource<bool> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<StarInteractionResponse> GenerateAsync(StarProfile profile, StarInteractionRequest request, StarState state, CancellationToken cancellationToken)
    { Called.TrySetResult(true); return Task.FromResult(new StarInteractionResponse { StarId=profile.StarId }); }
}
