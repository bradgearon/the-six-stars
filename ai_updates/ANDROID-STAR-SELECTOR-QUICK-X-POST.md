# Android Star Selector Quick X Posting

The production Android Star selector now exposes a `Post 5 to X` button on every Star card.

- Tapping the X button queues an immediate five-post batch for that specific Star without changing the selected companion.
- The quick-status line in the popup confirms whether the desktop accepted the queue request.
- The same quick X button is also available in the girlfriend Star settings popup, since tapping the active girlfriend sprite opens that panel instead of the normal selector.
- The existing desktop posting lane remains authoritative: batches are uninterrupted once started, duplicate manual batches for the same Star are rejected, and multiple Stars wait their turn through the existing serialized X posting service.
- A mobile request for a Star without an enabled X account/client configuration is rejected immediately instead of being reported as queued and failing later.
- X progress events for a different Star no longer overwrite the Android status for the currently selected Star.

This is a convenience surface only; it reuses the existing `PostStarWritingsToXNow` bridge command and OAuth/token/posting pipeline.
