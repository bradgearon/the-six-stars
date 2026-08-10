# Global Overall writing persistence fallback

## Behavior

Every Star keeps her existing character-specific writing persistence rules. In addition, the
writing service now applies one shared fallback rule after those rules are evaluated:

```text
Overall > 80
```

The comparison is intentionally strict. A score of exactly `80` does not qualify; `80.01` and
above do. A match is recorded as:

```text
global-overall-excellence
```

This catches unusually strong pieces whose strengths do not happen to fit a Star's specialized
rule shape. Character-specific rules remain the normal persistence path.

## Configuration

The shared threshold defaults to `80`. It can be changed without editing all six Star profiles by
setting:

```text
WDS_STAR_WRITING_GLOBAL_OVERALL_THRESHOLD
```

The value is parsed using invariant-culture decimal notation and clamped to `0..100`. Set it to
`100` to effectively disable the fallback.

## Existing notes

The fallback applies to newly reviewed writings. It does not rewrite the append-only historical
statement log or retroactively change older shelf decisions.

## Why 80

The supplied all-Star statement log contained 365 reviewed writings. At a strict `Overall > 80`
cutoff, only two rejected pieces would have been rescued: one Auerlis statement scored `85` and
one Nyxari statement scored `80.83`. A cutoff of `75` would have rescued several grammatically or
semantically weak statements, so `80` is the safer cross-Star backstop.
