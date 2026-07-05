# Deterministic Effectors + Met/Ally Star State

Adds durable gameplay-driven state for whether each Star has been met and whether she is an ally. Elusiaa defaults to met + ally. Other Stars default to not met and not ally until the host/game calls the runtime API.

## Runtime APIs

```csharp
await runtime.SetStarMetAsync(sessionId, "Kaelia", true, ct);
await runtime.SetStarAllyAsync(sessionId, "Kaelia", true, ct);
await runtime.SetStarWorldStateAsync(new StarWorldStateUpdateRequest
{
    SessionId = sessionId,
    StarId = "Kaelia",
    IsMet = true,
    IsAlly = true,
    Reason = "player met Kaelia after the Virethorn water puzzle"
}, ct);
```

## Deterministic effectors

Effectors are temporary or persistent modifiers applied to deterministic relationship updates. They are stored in `StarState.ActiveEffectors` and persisted in the JSON state file / Unity save chunk.

```csharp
await runtime.ApplyStarEffectorAsync(sessionId, "Elusiaa", new StarRelationshipEffector
{
    Id = "under_pressure_romance_rival",
    Mode = "under_pressure",
    DisplayName = "Under Pressure: Romance Rival",
    Source = "romance_system",
    Reason = "The Last Cat is romancing another Star nearby.",
    ConnectionGainMultiplier = 0.65,
    ConnectionLossMultiplier = 1.35,
    SuppressPassiveConnectionGain = true,
    TrustDeltaPerTurn = -1,
    BenaveilInfluenceDeltaPerTurn = 1,
    RemainingTurns = 5,
    Tags = { "romance", "pressure", "rival" }
}, ct);
```

Clear one or all:

```csharp
await runtime.ClearStarEffectorAsync(sessionId, "Elusiaa", "under_pressure_romance_rival", ct);
await runtime.ClearStarEffectorAsync(sessionId, "Elusiaa", "all", ct);
```

## Connection changes

Conversation attention now creates tiny passive connection gains when the player continues talking to an allied Star without negative signals. Strong good emotion signals build connection faster. Effectors can suppress or amplify those gains.

## Discovery gate

The prompt now receives `knownStarsMet`, `knownStarsAllies`, and `knownStarsDetail` from durable state. Stars should only speak about other Stars as known companions when the player has met those Stars.
