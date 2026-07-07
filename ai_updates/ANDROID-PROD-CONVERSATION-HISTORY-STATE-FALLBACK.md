# Android Prod Conversation History State Fallback

This update makes the production Android conversation window recover the selected Star's recent conversation history from the desktop Star state when the phone has no local in-memory lines yet.

## Changes

- `/api/mobile/state` now accepts a `sessionId` query parameter so Prod Mode can load `android-phone-prod` state instead of always reading `android-phone-dev`.
- The mobile state payload now includes `recentConversationHistory` for every Star.
- Mobile bridge state/change events can also carry `recentConversationHistory`.
- Android Prod Mode seeds the rolling conversation window from `recentConversationHistory` only when the selected Star has no local phone-side conversation lines.
- New local messages are inserted at the top of the rolling window instead of the bottom.
- The newest turn stays at the top; the phone trims old lines from the bottom.
- Clearing history now clears the phone-side prod conversation cache too.
- Prompt history now falls back to saved archived conversation entries when the active `MemoryLog` has no usable local turns.

## Expected behavior

When the Android app opens or reconnects:

1. It calls `/api/mobile/state?sessionId=android-phone-prod` in Prod Mode.
2. If the selected Star has no local conversation lines yet, Android loads the recent Star turns from desktop state.
3. The newest conversation appears first.
4. New `You:` and Star response lines appear at the top of the small rolling history window.

