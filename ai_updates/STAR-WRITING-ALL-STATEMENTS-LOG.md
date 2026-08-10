# Shared all-Star writing statement log

Every reviewed writing candidate is now appended to one durable file:

`state/writing-notes/all-star-writing-statements.jsonl`

The file uses JSON Lines: each line is one complete statement record, so the log can grow without rewriting its entire history on every background-writing cycle.

Each entry contains:

- the writing and its two source words;
- `StarId` and the human-readable `Speaker` name;
- generation time, batch ID, item ID, and index;
- the compact 27-score `EmotionalVector`;
- a named `Scores` object containing all 27 category scores;
- whether the statement reached the shelf;
- the persistence rules it matched.

The aggregate log records both persisted and rejected candidates. Clearing one Star's writing notes does not clear this historical log. The log begins with statements generated after this update; it does not invent or reconstruct older entries that are no longer present in per-Star state.

The existing per-Star files remain unchanged in purpose and continue to hold recent history, shelf items, told state, and persisted-word state.
