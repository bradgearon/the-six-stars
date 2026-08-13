# Generic actor profiles

Stars are now explicitly tagged as `actorType: "star"` and carry a `soul` capability block. Generic non-Star actors live in `src/Wds.SixStars.Api/Content/Actors/`.

An actor is enabled by one JSON file whose filename matches its `actorId` (case-insensitive) and whose `active` property is `true`. `FileActorProfileProvider` can merge these actors with the active Star catalog. The disabled `sample.json` demonstrates the package.

`ActorSoulConfig` lets actors have different depths of participation. Memory, player-quote capture, conversation, relationship state, writing, story seeding, story appearance, rumor participation, and free-form function names are independent capabilities. This is deliberately scaffolding for later story-appearance/rumor work; generic actors are not automatically inserted into Constellation Forge yet.
