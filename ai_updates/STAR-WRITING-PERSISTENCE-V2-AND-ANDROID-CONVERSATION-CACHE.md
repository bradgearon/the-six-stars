# Star Writing Persistence V2 + Android Conversation Cache

## Writing shelf

The Aug-14 exported shelf state showed a calibration regression: Elusiaa and Maera had saved compact Sparks while Auerlis, Khavia, and Nyxari had none, despite having reviewed Spark history. Kaelia had only one. The old per-Star Overall IU backstops no longer matched the local reviewer's current ranges.

- Recalibrated Spark Overall backstops: Auerlis 55, Elusiaa 40, Kaelia 50, Khavia 45, Maera 55, Nyxari 45.
- Bumped `StarWritingPersistencePolicy.CurrentVersion` from 1 to 2.
- On first load, policy V2 replays the append-only all-Star statement log locally and adds newly qualifying Sparks without another LLM call.
- Told/posting history on already-saved items remains intact.
- The canonical positional ReaderEmotion IU vector contract is unchanged.

Against the supplied Aug-14 statement corpus, the threshold route alone yields at least three eligible compact Sparks for every Star; the uploaded states therefore recover non-zero pending Spark shelves for the Stars that were stuck at zero.

## Android conversation history

The Prod conversation window had become an in-memory-only cache that relied on an asynchronous desktop state hydration after every activity/app restart. If that state response was delayed or empty, the UI showed `No conversation yet.` even though the user had just been talking to the Star.

- Added a tiny per-Star rolling conversation cache to Android `SharedPreferences` (12 display lines per Star).
- The visible conversation hydrates synchronously from the phone cache at startup and whenever the selected Star changes.
- Desktop `recentConversationHistory` remains canonical and is merged newest-first when it arrives.
- New phone-side conversation lines update the durable cache immediately.
- Clear History / Clear All Data also clears the matching phone-side history cache.

This restores immediate history display while keeping desktop Star memory as the authoritative long-term source.
