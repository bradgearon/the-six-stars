# Star Writing Shelf: Two-Word Jokes, Poetry, Rating, Persistence, and Performance

## Purpose

This feature gives each Star a durable creative-writing practice separate from ordinary chat. A Star can create five two-word jokes, aphorisms, or poetic fragments, have a second model pass evaluate them, retain only the pieces that fit her individual standards, and later choose up to five untold pieces to perform aloud.

The desktop and Android apps use the same canonical writing-notes files and the same three-request pipeline.

## User Interface

### Desktop

The **Quick Actions / Test Buttons** panel contains a **Star Writing Shelf** section:

- **Create 5 Jokes / Poems** — runs the generation and rating requests.
- **Tell Best / Latest (N)** — runs the performance request for up to five pending pieces.

The Tell button is disabled when the selected Star has no untold persisted writing. Both writing actions are disabled while a runtime request is active.

### Android

Tap the cat portrait to open the production companion popup. Its **Star Writing Shelf** section contains the same two actions:

- **Create 5 Jokes / Poems**
- **Tell Best / Latest (N)**

The Tell button is disabled when `pendingWritingCount` is zero. The popup also displays the number of new and saved pieces. The performance request can stream the resulting voice to the phone using the existing phone-audio path.

## Three AI Requests

### 1. Generate five writings

The first request receives:

- a Scriban-rendered creative-writing character card containing only writing-relevant identity, personality, backstory, voice, attributes, imagery, quotes, and canon details;
- the last ten individual writings;
- the complete historical list of chosen words.

It must choose exactly five pairs of substantially unrelated words and create one finished joke, aphorism, or poetic fragment from each pair.

Structured response:

```json
{
  "starId": "Nyxari",
  "jokes": [
    "How does a fragment greet an echo? With a quiet, knowing nod; they both know something is missing."
  ],
  "usedWords": [
    ["Fragment", "Echo"]
  ]
}
```

The schema requires exactly five joke/writing strings and exactly five matching two-word arrays. `jokes[i]` is always paired with `usedWords[i]`. The service converts those parallel arrays into internal candidate records before the review request.

The generator prompt is rendered from `star_writing_generate_system.sbn` and `star_writing_generate_user.sbn`. It deliberately excludes romance mode, girlfriend mode, relationship statistics, inter-Star scoring rules, emotion matrices, story decks, TTS clone configuration, and other unrelated profile data. The last ten writings are rendered as compact prose lines, while the complete used-word history is rendered as a simple word list rather than serializing the entire writing state.

### 2. Rate all five writings

A separate critic request receives a smaller Scriban-rendered evaluator card and the five candidates. Persistence thresholds are deliberately removed from both creative/evaluator prompt copies of the profile, so the model cannot aim at or inflate a save threshold; the deterministic service applies those rules afterward. It returns exactly five zero-based rating entries.

Each `emotionalVector` contains 27 entries in this fixed order:

1. Funny
2. Deep
3. Intrigued
4. Interested
5. Poetic
6. Clever
7. Beautiful
8. Moving
9. Memorable
10. Original
11. Witty
12. Playful
13. Surprising
14. Mysterious
15. Haunting
16. Melancholy
17. Hopeful
18. Romantic
19. Vivid
20. Coherent
21. Meaningful
22. Characterful
23. EmotionallyResonant
24. Polished
25. Quotable
26. WorthRetelling
27. Overall

The compact format is:

```text
score:confidence;score:confidence;...
```

- score range: `0-100`
- confidence range: `0-1`

Example response:

```json
{
  "starId": "Nyxari",
  "ratings": [
    {
      "index": 0,
      "emotionalVector": "72:0.86;91:0.92;80:0.78;..."
    }
  ]
}
```

The service normalizes malformed, missing, or out-of-range vector entries before using them. The category dictionary is created only in memory while applying persistence rules or selecting a voice emotion; it is never written into the notes JSON.

### 3. Perform best/latest pending writing

The third request receives:

- a compact Scriban-rendered performance character card;
- the entire persisted writing collection, including generation date, persistence date, ratings, and told status;
- the exact IDs currently eligible to be told.

The Star chooses exactly five pending IDs when five or more are available, otherwise all pending IDs, according to her own preferences, recency, quality, contrast, and character. She returns a one-to-one TTS-ready spoken array in the same order:

```json
{
  "starId": "Nyxari",
  "selectedWritingIds": ["writing-..."],
  "spokenText": [
    "I kept this fragment because it was unwilling to disappear. How does a fragment greet an echo? With a quiet, knowing nod; they both know something is missing."
  ]
}
```

Only pending IDs are accepted. If the model returns too few valid pending IDs, the service fills the remainder with the newest pending pieces. If its spoken array is not one-to-one with the final selection, the service safely falls back to the stored writing text. Selected pieces receive `ToldCount += 1` and `LastToldAt`.

## Writing Notes State

Default location:

```text
state/writing-notes/{StarId}-writing-notes.json
```

Override with:

```text
SIX_STARS_WRITING_NOTES_DIRECTORY
```

Each Star has a separate file containing:

- `recentWritingHistory` — rolling last ten individual generated/rated pieces, including pieces that did not persist;
- `allUsedWords` — complete case-insensitive historical word list;
- `persistedWritings` — durable pieces that passed at least one Star-specific rule;
- generation/persistence/told timestamps;
- normalized emotional vectors stored only as compact strings;
- persistence rule IDs that accepted each piece;
- told count and pending status.

Clearing a Star's history clears that Star's writing-notes file. **Clear All Star Data** clears all six writing-notes files.

## Prompt Context and Storage Revision

The writing pipeline uses six Scriban templates:

- `star_writing_generate_system.sbn`
- `star_writing_generate_user.sbn`
- `star_writing_review_system.sbn`
- `star_writing_review_user.sbn`
- `star_writing_performance_system.sbn`
- `star_writing_performance_user.sbn`

The generation prompt includes only the following Star fields:

- identity: display name, Star ID, element, domain, core quote;
- personality: short personality, deep personality, backstory summary, compact relevant background;
- writing voice: voice archetype, speaking style, general delivery direction;
- character pressure: named 0-100 attribute values and their notes;
- creative identity: behaviors, storytelling rules, representative quotes, selected Star secrets and canon notes;
- continuity: the rolling last ten writings and the complete historical used-word list.

It excludes romance/girlfriend settings, attraction hooks, flirt rules, relationship state/scoring, emotion influence maps, combo/inter-Star rules, voice-clone files, skill trees, people/contact lists, and unrelated story data.

Writing-notes schema version 2 stores `EmotionalVector` as the sole rating representation. Existing version-1 files containing `EmotionalValues` remain readable because unknown legacy JSON fields are ignored. The next save rewrites the file in version-2 form without the expanded object.

## Per-Star Persistence Rules

Each Star JSON profile now has `writingPersistenceRules`. A writing is persisted when at least one rule matches. Requirements within a rule use `matchMode: "all"` by default; `"any"` is also supported.

Example:

```json
{
  "id": "fragmentary-metaphysical-wit",
  "matchMode": "all",
  "requirements": [
    {
      "category": "Poetic",
      "minimumScore": 82,
      "minimumConfidence": 0.72
    },
    {
      "category": "Haunting",
      "minimumScore": 72,
      "minimumConfidence": 0.66
    }
  ]
}
```

The supplied profiles emphasize different creative identities:

- **Nyxari:** poetic/haunting fragments, deep quotable aphorisms, brittle shadow wit.
- **Kaelia:** melancholy depth, fatal beauty, cold originality.
- **Elusiaa:** hopeful meaning, moving beauty, gentle play.
- **Khavia:** reconnecting hope, friendly play, moving tide imagery.
- **Auerlis:** funny/playful fire, vivid surprise, hopeful retellability.
- **Maera:** emotional resonance, possessive depth, sharp blood wit.

If a profile has no writing rules, the service uses a conservative fallback: `Overall >= 75` with confidence `>= 0.70`.

## Mobile Bridge Commands and Events

Commands:

- `GenerateStarWriting`
- `TellStarWriting`

Aliases are normalized by `MobileBridgeDispatcher`.

Events:

- `StarWritingGenerated`
- `StarWritingPerformanceReady`

Writing events include:

- `pendingWritingCount`
- `persistedWritingCount`
- `writingItems`
- `spokenText` for a performance

Writing counts are nullable on unrelated generic bridge events so ordinary state updates do not accidentally reset the Android shelf count. Select-Star, refresh, clear-history, and writing events explicitly include current counts.

## Main Implementation Files

### API

- `src/Wds.SixStars.Api/Writings/StarWritingModels.cs`
- `src/Wds.SixStars.Api/Writings/IStarWritingAgent.cs`
- `src/Wds.SixStars.Api/Writings/StarWritingFileStore.cs`
- `src/Wds.SixStars.Api/Writings/StarWritingService.cs`
- `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`
- `src/Wds.SixStars.Api/Ai/StarResponseJsonSchema.cs`
- `src/Wds.SixStars.Api/Agents/MockStarAgent.cs`
- `src/Wds.SixStars.Api/Stars/StarProfile.cs`
- `src/Wds.SixStars.Api/Content/Stars/*.json`
- `src/Wds.SixStars.Api/Mobile/MobileBridgeDispatcher.cs`

### Desktop

- `src/Wds.SixStars.Ui/MainWindow.xaml`
- `src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`
- `src/Wds.SixStars.Ui/ViewModels/MainViewModel.MobileBridge.cs`

### Android

- `android/six-stars-mobile/app/src/main/java/com/wds/sixstarsmobile/MainActivity.java`

## Validation Performed in the Delivery Environment

- All six Star JSON profiles parse successfully and contain three persistence rules each.
- `MainWindow.xaml` parses as valid XML.
- Delimiter/string/comment-aware static checks pass for all modified C# and Java files.
- `javac` parses `MainActivity.java` through syntax analysis; compilation then stops at the expected missing Android SDK packages.

A full .NET build could not be executed because the delivery environment does not contain the .NET SDK. A full Android Gradle build could not be executed because the wrapper distribution is not locally cached and outbound network access is unavailable.
