# Star initiative + interstar reply update

This update adds two companion-behavior layers on top of the daily JSON state and rolling emotion trace logs.

## Daily state wants-to-talk-about list

Daily Star state generation now asks each Star for `thingsSheWantsToTalkAbout`, and the stored daily JSON exposes it under:

```json
"today": {
  "wantsToTalkAbout": []
}
```

These are short, domain-specific topics the Star may naturally bring up later during an at-rest initiative check.

## Interstar one-sentence replies

When one Star relays a meaningful Last Cat moment to another Star, the destination Star now returns:

```json
"replyToSourceStar": "..."
```

That line is not spoken to the player. It is the one sentence the destination Star would say back to the source Star based on:

- how friendly the two Stars are,
- how emotionally relevant the relay is to the destination Star,
- how close the destination Star is with the Last Cat,
- whether the destination Star would realistically share vulnerability, excitement, caution, or only a guarded acknowledgement.

The runtime includes these replies in interstar status, the interstar cycle summary, and the rolling conversation emotion trace log.

## At-rest Star initiative

The desktop dev UI now has:

- `Auto Star at-rest lines`
- `Ask Star If She Wants To Speak`

When enabled, the desktop app occasionally asks the selected Star, through the normal spoken request path, whether she has anything she wants to say to the Last Cat while he is quiet. The prompt uses the normal Star state, daily JSON, recent emotions, interstar feedback, and relationship posture. The Star is instructed not to mention systems, JSON, vectors, reconnects, or initiative checks.

Android Prod has the same toggle in the cat profile menu:

- `Let the selected Star speak at rest`

When enabled, Android may ask for an at-rest line after the phone voice socket reconnects. The request returns through the normal `/api/mobile/dispatch` path, streams voice to the phone if enabled, and keeps the screen awake while the Star is answering.

## Notes

- This is default-off unless `SIX_STARS_STAR_INITIATIVE_ENABLED=true` is set for the desktop app.
- The Android toggle is saved in `SixStarsMobilePrefs`.
- At-rest lines use `starInitiative=true` in the request game-context flags so the prompt builder can distinguish them from a real player command.
- Newest-first Android conversation ordering remains preserved.
