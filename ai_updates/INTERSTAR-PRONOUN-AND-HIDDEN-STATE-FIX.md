# Interstar Pronoun + Hidden State Fix

## Why
Conversation-emotion traces showed interstar replies occasionally leaked direct player-facing language into Star-to-Star speech:

- Elusiaa addressed other Stars as `little one`, even though that nickname is for the Last Cat.
- Some relay lines used `you` when they meant the Last Cat, making it sound like the target Star was the player.
- Some model output treated the Last Cat with `she/her` pronouns.
- Interstar micro-updates with empty spoken text were polluting normal conversation summaries with `The Star replied: ""`.
- Interstar all-zero emotion vectors could still apply small connection drift or legacy relationship combos because the micro-request reused the original player text.

## What changed

### Prompt fixes
Interstar relay, reaction, and feedback prompts now explicitly require:

- The Last Cat = `the Last Cat`, `he`, `him`, `his` unless host state says otherwise.
- `you` only means the Star being addressed in Star-to-Star speech.
- Other Stars should not be addressed as `little one`.
- Relay `summary`, `reason`, and `cooldownKey` should be non-empty when `shouldTell=true`.

### Runtime safeguards
The interstar parser now normalizes common slips:

- Leading `Little one,` in a Star-to-Star relay becomes the target Star name.
- `with/about/of little one` becomes `with/about/of the Last Cat`.
- Common sentence-level `The Last Cat... She/Her` slips become `He/His`.
- Empty relay `summary`, `reason`, and `cooldownKey` get deterministic fallbacks.

### State hygiene
Hidden interstar micro-updates now:

- Do not append direct conversation turns to `MemoryLog`.
- Do not replace the prompt-facing `ConversationSummary`.
- Do not increment the direct `InteractionCount` or `LastInteractionAt`.
- Do not overwrite the last direct emotional vectors/top emotions.
- Can still apply explicit hidden relationship deltas when the interstar vector actually contains evidence.

### Scoring hygiene
For `interstarMicroUpdate=true`:

- Legacy/profile relationship combos are disabled.
- All-zero interstar vectors no longer create baseline connection drift.
- Any real nonzero interstar vector can still produce a modest hidden relationship update.
