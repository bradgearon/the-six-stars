# Android Listen Button / Manual Star Initiative

Adds a compact icon-only **Listen** button to the Android production companion UI, placed directly to the right of the main Talk button.

Behavior:
- Sends a `MaybeStarInitiative` command to the desktop bridge through `/api/mobile/dispatch`.
- Uses `RequestType=StarInitiative`, empty `playerText`, and `clientName=SixStarsMobileAndroid-ManualListen`.
- The request reason is `manual Android Listen button`, so the desktop/API path sets `starInitiativeForce=true` and treats it like the desktop “ask if she has anything to say” button.
- Pauses background at-rest initiative checks before sending the manual request so the player-initiated Listen button has priority.
- Keeps the phone awake while the Star is thinking and still relies on queued bridge events if the HTTP wait is interrupted.
- Uses a new `ui_listen_pixel` icon with no text on the button.

Runtime prompt behavior:
- Manual Listen/A-button mode now tells the Star to behave like the Last Cat pressed interact beside an NPC.
- The Star should prefer to produce a short fresh line from daily state, relationship posture, girlfriend wants/needs, recent conversation, or current feelings unless doing so would violate canon or repeat herself.
- Forced initiative now always passes the pre-LLM state gate, even if daily state is still sparse, so the button can still work like an NPC interaction prompt.
