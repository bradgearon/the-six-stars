# Star initiative state gate + response type

This update changes at-rest Star initiative from a fake Chat turn into a dedicated `RequestType.StarInitiative` system check.

## Behavior

- Android/background/desktop at-rest checks send `RequestType=StarInitiative` instead of `Chat`.
- `PlayerText` is blank for these checks. The prompt explicitly says this is not a player message.
- The runtime inspects attached daily JSON / Star state before asking the LLM.
- If the state does not contain a reason to interrupt the Last Cat, the runtime skips the LLM and returns no speech.
- Star initiative checks do not run background emotion review, memory update, relationship deltas, or interstar ripples.

## New response shape

The foreground Star initiative pass uses a dedicated JSON response format:

```json
{
  "starId": "Elusiaa",
  "hasSomethingToSay": true,
  "priority": 0.62,
  "selectedTopic": "a daily-state topic or hook",
  "reason": "why she wants to say it now",
  "spokenText": ["one or two chunks she says naturally"]
}
```

When there is no state-backed reason to speak, the LLM should return:

```json
{
  "starId": "Elusiaa",
  "hasSomethingToSay": false,
  "priority": 0,
  "selectedTopic": "",
  "reason": "no state-backed reason to speak now",
  "spokenText": []
}
```

## Android

- Background service sends `requestType=StarInitiative`.
- Direct Android at-rest check sends `requestType=StarInitiative`.
- If the response says `initiativeShouldSpeak=false`, Android does not add a fake conversation line or play audio.
- The cat menu toggle still controls whether these checks happen.

## Debug fields

Responses/events now include:

- `initiativeShouldSpeak`
- `initiativeSkippedBeforeLlm`
- `initiativeStateScore`
- `initiativePriority`
- `initiativeTopic`
- `initiativeReason`

