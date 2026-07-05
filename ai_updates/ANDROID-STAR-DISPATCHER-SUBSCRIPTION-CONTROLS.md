# Android Star Dispatcher Subscription Controls

This update moves Android Star selection/state controls behind an API-layer dispatcher instead of letting the Android bridge directly mutate view-model state.

## Added API dispatcher

New API-layer type:

- `Wds.SixStars.Api.Mobile.MobileBridgeDispatcher`
- `MobileBridgeDispatchCommand`
- `MobileBridgeDispatchResult`
- `MobileBridgeDispatchEvent`
- `MobileBridgeEventBatch`

The desktop `MainViewModel` registers one command handler and listens for published dispatcher events. This keeps the HTTP bridge thin and allows Android, desktop, Unity, or future clients to share the same control/event path.

## New mobile endpoints

- `GET /api/mobile/state` returns selected Star plus per-Star met/ally/relationship state for the Android bridge session.
- `POST /api/mobile/dispatch` accepts queued commands:
  - `SelectStar`
  - `SetMet`
  - `SetAlly`
  - `ClearHistory`
  - `RefreshState`
- `GET /api/mobile/events?after=<sequence>&max=25` returns queued dispatcher events for the Android front end subscription loop.

## Android UI

The Android client now has:

- Star dropdown wired to `SelectStar` dispatch commands.
- Toggle buttons for met and ally state.
- Clear History button for the selected Star.
- A small Star state line showing met/ally/heart/trust/romance/connection.
- A polling subscription loop that receives queued dispatcher events and updates the front end when the selected Star or Star state changes.

## MainViewModel behavior

Desktop selected Star changes also publish a `SelectedStarChanged` dispatcher event, so Android can follow desktop-side selection changes through the same queued event feed.
