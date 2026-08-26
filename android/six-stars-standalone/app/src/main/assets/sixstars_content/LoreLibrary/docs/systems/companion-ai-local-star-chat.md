# Companion AI / Local Star Chat Engine

## Purpose

The Six Stars should feel like living companions, not scripted menus. After meeting and equipping a Star, the player can speak to her during exploration, rest, combat pauses, or safe-room moments.

## Core Flow

1. Player says a hotword such as “Elusiaa,” “Kaelia,” or “Nyxari.”
2. Hotword detector opens the microphone.
3. Speech-to-text captures the player line.
4. Game builds a context packet.
5. Local fine-tuned small LLM generates a structured response.
6. TTS reads the `spoken_response`.
7. Relationship/emotion data updates.
8. UI shows small feedback if a threshold or combo triggers.

## Context Packet

Feed the local model:

- Current area/room.
- Equipped Stars.
- Current objective.
- Recent battle data.
- Letters read and returned.
- Settlements visited.
- Major moral choices.
- Current relationship state.
- Romance mode and intensity.
- Secrets available to this Star.
- Important lore facts unlocked.

## Use Cases

### Exploration Banter

The player can ask about the cave, lore, enemies, puzzles, or feelings.

### Combat Commentary

A Star can react to battle data:

- Number of slimes defeated.
- Damage taken.
- Parries timed.
- Boss phase reached.
- Secret room discovered.

Example:

> “Sixteen slimes in two minutes… wow, handsome.”

### Puzzle and Guardian Clues

Nyxari may know one clue about a hidden guardian. She should not reveal everything. A good clue is poetic and directional:

> “Some of the old guardians still sleep in the places the light abandoned. One of them rests where the bones remember the first song.”

### Relationship and Romance

Emotional chains can open secrets, banter, stronger ally trust, or girlfriend eligibility.

### Wisdom Training

AI scenes should often test whether the player can slow down, investigate, comfort, or redirect a Star. The game rewards wisdom without making every wise answer obvious.

## Safety / Consistency Guardrails

- The model must remain in-character.
- It should not reveal locked spoilers.
- It should not contradict canon documents.
- It should not sexually escalate unless Romance Mode and age/maturity settings allow flirty content.
- It should not solve every puzzle directly.
- It should summarize memories compactly for save files.

## Recommended Output Fields

- `spoken_response`
- `emotion_scores`
- `relationship_delta`
- `wisdom_delta`
- `romance_delta`
- `dark_force_delta`
- `secret_unlock_hint`
- `quest_flag_updates`
- `memory_summary`
- `ui_feedback`
