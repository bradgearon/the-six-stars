# Connection Flirting + Authored Dialogue State API

This update adds a state-aware romance/flirting layer and authored dialogue memory/progress storage.

## Connection-based flirt behavior

The prompt now reads the Star's `EmotionalConnectionPercent`, Heart/Trust/Romance, met/ally flags, girlfriend readiness, and active deterministic effectors. As connection rises, the Star is instructed to become more openly flirty in her own style while still respecting ally status, earned closeness, and story timing.

Each default Star profile now has private internal attraction hooks:

- Elusiaa: drawn to the Last Cat's good heart, mercy, restraint, and hope.
- Kaelia: privately drawn to his warm fur, purring, and impossible gentleness as a sentient Last Cat.
- Maera: drawn to his poetry, powerful presence, and controlled intensity.
- Khavia: drawn to his friendship, bridge-building, and care for communities.
- Auerlis: drawn to his spark, humor, music, and brave playfulness.
- Nyxari: could like him once he proves maturity by helping her village and acting with patient meaning.

These are internal prompt hooks, not cheap confessions. The AI should use them as subtext and only reveal deeper attraction when state supports it.

## Global canon added to prompts

The Stars now know the core Last Cat setup:

- No cat has been seen for one hundred years.
- The universe is drying up because cats are gone.
- The Last Cat was delivered twenty years ago to an intergalactic orphanage.
- The six Stars are human women in/around the caves and settlements.
- They have childhoods, parents, village ties, and father-forms/spiritual parent-forms.
- They do not know the hidden angel is named Benaveil unless the game explicitly reveals it.
- Bradmageddon is alien/outside their world; they may know the name but not who he truly is.
- Angelic forms sometimes appear in cave rumors, walk down corridors, and vanish.
- Other Stars remain discovery-gated by met/ally state.

## New persistent state fields

`StarState` now includes:

```csharp
public List<StarDialogueMemoryEntry> GameDialogueHistory { get; set; }
public StarDialogueProgressState CurrentDialogueState { get; set; }
```

## Add one completed authored dialogue beat

Use this after an in-game cutscene line, settlement scene, companion banter, or dialogue node finishes:

```csharp
await runtime.AddStarDialogueEntryAsync(new StarDialogueMemoryUpdateRequest
{
    SessionId = "save-slot-1",
    StarId = "Elusiaa",
    Entry = new StarDialogueMemoryEntry
    {
        DialogId = "virethorn_intro_01",
        NodeId = "elusiaa_warns_about_water",
        Title = "Elusiaa warns about the poisoned water",
        Speaker = "Elusiaa",
        Text = "Do not drink from that stream, little one. It is carrying someone else's wound.",
        Summary = "Elusiaa warned the Last Cat that the water is poisoned and emotionally tied to the cave's wound.",
        SceneId = "level_one",
        Area = "Virethorn Caves",
        QuestId = "cleanse_poisoned_water",
        SequenceIndex = 3,
        IsComplete = true,
        Tags = { "virethorn", "water", "warning" }
    }
}, ct);
```

## Set the whole authored dialogue/script progress

Use this when Unity has a full dialogue tree/script and the Star needs to know where she is inside it:

```csharp
await runtime.SetStarDialogueStateAsync(new StarDialogueProgressUpdateRequest
{
    SessionId = "save-slot-1",
    StarId = "Elusiaa",
    DialogueState = new StarDialogueProgressState
    {
        DialogId = "virethorn_intro_01",
        Title = "First lower-caves conversation",
        SceneId = "level_one",
        Area = "Virethorn Caves",
        QuestId = "cleanse_poisoned_water",
        CurrentNodeId = "player_asks_about_slime_gel",
        CurrentLineIndex = 12,
        CurrentStage = "investigating poisoned water",
        SummarySoFar = "The Last Cat met Elusiaa, noticed slime gel, and is learning why the lower cave water is poisoned.",
        LastAuthoredLine = "Do not call something food merely because it fought you.",
        NextExpectedBeat = "Elusiaa should ask him to investigate the water source before judging the goblins.",
        FullScriptText = "Optional full authored dialogue text or markdown.",
        FullScriptJson = "Optional full authored dialogue JSON from the game.",
        IsActive = true,
        IsComplete = false,
        Tags = { "intro", "elusiaa", "water" }
    }
}, ct);
```

Clear current dialogue progress:

```csharp
await runtime.ClearStarDialogueStateAsync("save-slot-1", "Elusiaa", ct);
```

Dialogue state is persisted in the same JSON save/state file as met/ally/effectors, so the next app launch will know where the Star is in the authored story.
