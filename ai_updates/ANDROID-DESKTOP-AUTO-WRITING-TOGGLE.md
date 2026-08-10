# Android desktop auto-writing toggle

The Last Cat Console opened from the Android player/cat icon now includes a checkbox for the desktop **Generate for every Star while idle** setting.

## Behavior

- The Android checkbox reads the authoritative setting from the connected desktop bridge.
- Changing it sends `SetBackgroundWritingLoopEnabled` immediately; the separate popup **Save Settings** button is not required.
- Turning it off cancels the current desktop background writing pass and stops new automatic generations.
- Manual **Create 5 Jokes / Poems** and **Tell Best / Latest** actions remain available.
- Turning it back on restarts the existing desktop idle loop behavior after the runtime has been clear for a few seconds.
- The desktop checkbox and Android popup remain synchronized through state payloads and bridge events.

This provides a quick recording mode: disable automatic writing before capturing a Star quote so background LLM work cannot compete with voice generation or playback.
