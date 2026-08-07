# Full Reset State and Daily XML Fix

## Problem

After pressing **Clear All Star Data**, the visible relationship flags could reset, but a later LLM request could still contain old Heart/Trust/Romance values in the generated daily Star XML or another front-end session.

Two stale-state paths caused this:

1. Desktop clear-all reset `desktop-local-dev` and `android-phone-dev`, but not `android-phone-prod`.
2. The daily Star XML cache is stored separately from the main Star state. If the current daily slot had already been generated, the runtime reused the XML even after the relationship state reset. That XML can contain old values like `heart="2" trust="3"`.

## Fixes

- Clear selected history and clear-all now reset every known local session:
  - `desktop-local-dev`
  - `android-phone-dev`
  - `android-phone-prod`
  - `local-dev`
- Reset now clears the generated daily XML folder for the affected Star/session.
- A reset now marks `LastDailyStateSourceUpdateAt`, so any remaining daily XML generated before the reset is considered stale.
- `StarDailyStateService` now regenerates same-slot daily XML when the underlying Star state changed after the XML was generated.
- Daily-state fallback from other sessions now skips stale records that predate the current Star state's source update time.
- Default reset state explicitly clears `TodayState` and marks the source update time.

## Result

After **Clear All Star Data**, the next LLM request should not receive old relationship numbers from:

- relationship state,
- score accumulators,
- conversation memory,
- Android prod/dev state,
- daily XML,
- or fallback daily XML from another session.

Elusiaa still defaults to met + ally by design. The other five Stars default to not met / not ally unless manually marked again.
