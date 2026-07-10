# All-Stars Foreground Prompt Cleanup

This update applies the cleaned girlfriend and foreground speech prompt wording through the shared `StarPromptBuilder`, so it affects Elusiaa, Kaelia, Khavia, Auerlis, Maera, and Nyxari consistently.

## Changed

- Girlfriend responses may use magical special effects and should make the Last Cat feel close and wanted.
- Removed the duplicate girlfriend-mode non-explicit / non-graphic instruction while retaining the existing shared relationship-level guidance.
- Removed the girlfriend-mode recent-line repetition warning from normal chat prompts.
- Removed fixed normal-chat spoken chunk counts and the extra direct-answer / warm-answer contract lines.
- Kept the only necessary TTS formatting rule: each `spokenText` item is one or two natural sentences.

## Scope

The changes live in the common prompt builder rather than any one Star profile, so all six Stars receive the same cleanup automatically while preserving their individual flirt and cute-mode instructions.
