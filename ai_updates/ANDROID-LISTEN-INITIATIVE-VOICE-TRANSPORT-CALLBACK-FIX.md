# Android Listen / at-rest voice transport callback fix

Manual Listen, automatic at-rest initiative, and photo commands travel through the mobile
`dispatch` operation. Their conversation response is wrapped in `MobileBridgeDispatchResult`.

After text-first delivery was introduced, phone TTS was stored in an
`AfterTransportResponseAsync` callback on `MobileBridgeResponse`. The dispatch wrapper copied the
serialized response fields but discarded that non-serialized callback. Android therefore received
and displayed the Star's text while no voice generation ever began. Normal typed/voice messages
continued to speak because they use the direct `message`/`audio` operations, which retained the
callback.

The dispatch result now owns the same post-transport lifecycle. HTTP and WebSocket dispatch paths
start voice only after the correlated response is written, or release pending analysis if transport
fails. This also fixes the same lost-audio path for dispatch-based photo responses.
