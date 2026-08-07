# Conversation History State Fallback Compile Fix

Fixes two compile errors introduced by the conversation-history fallback update.

## Fixed

- `BuildRelationshipStage` did not exist in `StarPromptBuilder`.
  - The interstar profile prompt now calls the existing `BuildRelationshipStatus(state)` helper.

- `CreateMobileRecentConversationHistoryFromResponse` was referenced from the mobile bridge response builder but was not implemented.
  - Added the missing helper.
  - It creates an immediate newest-first conversation item from the current Android request/response so the phone UI can show the latest message even before a later state event refresh arrives.
  - Kept the existing state/archive history fallback helpers intact.

## Behavior

- Android Prod conversation history remains newest-first.
- If the phone has no local history, `/api/mobile/state` and Star state events can still hydrate recent history from saved Star state.
- The immediate HTTP response now also includes the current turn as a recent conversation item.
