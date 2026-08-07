# Android Cute Mode Active Instructions + Compact Today State

## Problem

The Android **Cute now** button correctly forced `cuteMode=true`, but the foreground LLM prompt only showed a compact status line. It did not clearly tell the selected Star how to behave when cute mode is active, and the normal chat foreground prompt still injected the full raw `TODAY STAR STATE JSON`.

## Fixes

- Normal foreground chat now injects `TODAY STAR STATE — COMPACT CURRENT SIGNALS` instead of the raw daily-state JSON blob.
- The compact today-state block extracts only useful fields:
  - posture
  - situation
  - feelings
  - desires
  - current activity
  - recent people/events
  - mood tags
  - intentions
  - wants to talk about
  - conversation hooks
  - private tone hooks
- The raw daily state remains available to host/debug code, but is no longer pasted into the LLM prompt.
- Girlfriend cute mode now has explicit active-turn instructions:
  - when `cuteMode=true`, the next spoken response should visibly use cute mode unless the player asked a strict technical/system question or safety requires seriousness.
  - answer the player first, then include one cute-mode beat.
  - cute-mode beat = one physical/textual cute action or sound + one fitting pet name + one emotionally relevant sentence.
- Added selected-Star cute-mode instructions for each Star:
  - Elusiaa: light names, halo boops, tender blessings.
  - Kaelia: quiet-star names, void pocket privacy, reluctant softness.
  - Khavia: ocean sounds, explained touch, flowing pet names.
  - Maera: sparse “mine,” heartbeat/red-thread affection, tenderness through intensity.
  - Auerlis: spark teasing, tackle hugs, tiny fireworks, “law of Auerlis” jokes.
  - Nyxari: shadow vanish-kisses, poetic names, fragment compliments, private darkness.
- Girlfriend hourly compact state now includes `cuteModeActiveInstruction` when cute mode is active so the prompt has a direct instruction near the status line.

## Expected result

When Android forces cute mode active and the player says something like “What are you up to right love cute muffin,” Kaelia should not only answer with abstract tar-still water mood. She should answer the question and include a Kaelia-specific cute mode beat, such as a private void pocket, a quiet-star pet name, a reluctant forehead touch, or another guarded-soft action.
