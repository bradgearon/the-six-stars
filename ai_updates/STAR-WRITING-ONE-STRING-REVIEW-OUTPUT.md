# Star writing review: one string per writing

The review request now returns exactly five JSON string values:

```json
{"0":"70:0.9;55:0.8;...","1":"...","2":"...","3":"...","4":"..."}
```

Each string contains the 27 category ratings in the configured order. This removes the JSON array and per-entry quoting/comma overhead that caused the review response to generate roughly 1,300 completion tokens.

The stored writing-note representation remains unchanged: one `EmotionalVector` string per writing.

The parser still accepts the prior array formats for compatibility. It also repairs the observed 26-pair Kaelia response by deriving `Overall`, trims an unlabeled 28th pair, and accepts a colon-only chain when the model omits semicolons.

The review response remains compact, and no per-request completion-token ceiling is sent. The backend/model may finish naturally without an application-added `max_tokens` cutoff.
