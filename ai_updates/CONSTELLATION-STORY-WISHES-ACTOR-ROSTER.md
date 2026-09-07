# Constellation Forge story wishes + configurable actor roster

## Fast story-wish seeding

Before the viewpoint author creates an outline, every active writing-enabled Star participates in the existing light seed pass. Alongside fresh subjects/nouns and verbs, each Star now returns exactly five **story wishes**: concrete things she would especially like to hear happen, appear, or be explored in a story at that moment.

Wishes can be events, battles, romance beats, dangers, jokes, mysteries, relationships, spectacles, images, odd interruptions, or other specific desires. They are intentionally ephemeral creative-room input rather than a new historical repetition counter. Each wish is stored as structured provenance (`sourceActorId`, `sourceDisplayName`, `sourceActorType`, `wish`) so the same channel can accept wishes from slimes or other souled actors later without parsing a display string.

The complete team wish list is supplied to the viewpoint outline, every collaborative build phase, and the viewpoint final revision, and the final shelf/history/log record retains the cast, story subjects, and story-wish provenance for later analysis and actor appearance work. Prompts explicitly say the wishes are invitations: authors may use, combine, transform, or ignore them rather than treating them as a checklist.

## Author is not automatically the protagonist

Constellation Forge now distinguishes **viewpoint author** from story narrator/protagonist. Forge shelf stories are standalone imaginative fiction and are **non-canon by default**. Star profile/backstory/canon material can inform voice and world texture but does not turn invented events into Last Cat canon.

The ordinary foreground `RequestType.Story` system remains deliberately canon-respecting for in-game/player storytelling. Forge is a different writing pipeline and no longer inherits that intent semantically.

The outline/build/final instructions also explicitly prevent companion behavior such as `little one` from silently converting an unnamed reader or protagonist into The Last Cat. The Last Cat may still be an intentional named story character.

## Active Star roster

`Content/Stars/*.json` is now the authoritative configured Star roster when those files exist. Each Star profile has:

- `active`
- `sortOrder`
- `actorType`
- `actorId`
- `soul`

The Forge, desktop Star list, background writing rotation, wake aliases, Whisper name hinting, and the mobile `/api/mobile/stars` roster derive from active JSON-backed profiles instead of assuming exactly six names. Constellation Forge requires at least three writing-enabled Stars because its pending-finish protocol requires a viewpoint author plus a requesting builder and a different confirming builder.

The Android app refreshes its Star ID list from `/api/mobile/stars`. The six existing Stars retain their authored portraits/animation metadata; a newly configured Star without Android art gets a generic visual placeholder until custom UI art/metadata is added.

JSON configuration errors are no longer hidden by silently falling back to the built-in six-Star roster. The built-in provider is used only when no JSON Star roster exists.

## Generic souled actors

`Content/Actors/*.json` and `FileActorProfileProvider` establish the next actor layer. An active actor JSON file whose filename matches `actorId` registers that actor in the generic actor catalog. `ActorSoulConfig` allows different depths/capabilities for memory, player quotes, conversation, relationship state, writing, story seeding, story appearance, rumor participation, and named functions.

Stars can be projected into the same actor model. A slime or minor NPC can therefore have a much shallower soul package than a companion Star while still contributing story wishes, being eligible for story appearances, or participating in rumors later.

Generic actors are **not automatically inserted into Forge stories in this update**. This commit creates the package/catalog boundary so the next story-actor step can calculate appearances and feed selected actors into outline/build/canon/rumor processes without redesigning Star identity again.

## Dynamic roster cleanup

The foreground story lore capsule no longer embeds the original six Star names in code. The host supplies a compact summary of the currently active JSON Star roster. Whisper's live host paths likewise build name hints from the active roster; the options-class fallback is now generic rather than fixed to six names.

Actor IDs are globally unique across Stars and `Content/Actors`. If an active generic actor tries to reuse an active Star's `actorId`, startup discovery fails loudly instead of silently replacing one actor with another.
