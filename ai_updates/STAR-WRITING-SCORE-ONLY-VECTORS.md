# Star Writing Score-Only Vectors

The Star-writing review and persistence pipeline now uses one score per category and no confidence value.

## Wire format

Each of the five review properties is one compact string containing exactly 27 semicolon-delimited scores:

```json
{"0":"35;72;68;...;74","1":"...","2":"...","3":"...","4":"..."}
```

The scores follow the existing category order and the final score is `Overall`. No completion-token ceiling was added.

## Persistence

- Writing persistence requirements now compare only `MinimumScore`.
- All `minimumConfidence` fields were removed from the six writing-persistence configurations.
- Voice-emotion selection ranks the stored writing categories by score alone.

## Existing writing notes

Writing-note state is now version 3. Existing `score:confidence;...` vectors are accepted, their confidence values are discarded, and they are normalized to score-only strings on load/save. This migration is deterministic and makes no LLM request.

The property name `EmotionalVector` remains unchanged so existing JSON files and UI/debug consumers remain compatible.
