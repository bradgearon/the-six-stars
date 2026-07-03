# Fast Emotional Effect Vector Update

This update adds a single compact foreground emotion field for speed testing without restoring the heavy `emotionScores` object to the fast speech pass.

## New field

`emotionalEffectVector`

The field is a string that tells the host how the player's latest statement made the selected Star feel.

Format:

```text
confidence:score;confidence:score;confidence:score;...
```

Rules:

- One pair per emotion.
- The pairs must appear in the canonical emotion order.
- `confidence` is `0.00` to `1.00`.
- `score` is `0` to `100`.
- Use `0.00:0` when the emotion is not implied.
- No emotion names, spaces, reasons, brackets, or extra text inside the string.

Canonical order:

1. Happy
2. Amused
3. Playful
4. Comforted
5. Safe
6. Protected
7. Trusting
8. Respected
9. Admiring
10. Affectionate
11. Vulnerable
12. Hopeful
13. Wondrous
14. Intrigued
15. Impassioned
16. Determined
17. Concerned
18. Confused
19. Unsure
20. Frustrated
21. Angry
22. Hurt
23. Sad
24. Melancholy
25. Lonely
26. Guilty
27. Suspicious
28. Flattered
29. Attracted
30. Tender
31. Yearning
32. Jealous

## Schema changes

The fast foreground schemas now require `emotionalEffectVector`:

- normal chat foreground response
- story foreground response
- legacy full structured response

The background review schema is unchanged so the compact background emotion/memory pass can still choose only the most impactful `emotionScores` later.

## Runtime changes

`StarSpeechModelResponse`, `StarStructuredModelResponse`, and `StarInteractionResponse` now carry `EmotionalEffectVector` / `emotionalEffectVector`.

The debug output now includes the vector so latency and output-size behavior can be inspected.
