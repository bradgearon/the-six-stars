# Shelf Listen Selection: Sparks and Constellation Stories

The production Listen control now has separate shelf playback paths for compact Sparks and long-form Constellation Stories.

## Spark playback

1. Only untold `spark` artifacts are eligible.
2. The deterministic writing ranker reduces the shelf to the strongest 100 untold Sparks by saved open-ended IU review values.
3. A 25-item listening set is built from that elite pool: the peak anchors are guaranteed, most remaining slots are sampled without replacement with IU-weighted odds, and a couple of medium-valued items from the lower part of the top-100 pool provide contrast.
4. The 25 items are shuffled before the Star sees them. Each includes full Spark text plus Overall IU and deterministic public IU.
5. The Star chooses exactly five favorites and returns one spoken part for each.
6. Only those selected Sparks are marked told.
7. If the Star-selection call fails or violates the ID/spoken-part contract, the request fails visibly and no substitute selection is silently consumed.

## Constellation Story playback

1. Only unplayed `constellation-story` artifacts are eligible.
2. Story selection is deterministic and requires no LLM candidate-reading pass: choose the unplayed story with the highest saved **Overall IU** value. Deterministic public IU, persistence time, and ID only break exact ties.
3. No list of full story texts is sent for selection. The selected title/value can be reported to the UI, and only that one selected story is sent in full to the playback formatter.
4. The LLM receives the already-finished story and is instructed to preserve it exactly as writing while dividing the complete narration into an unbounded ordered `spokenText` array of natural TTS/breath chunks. It is a delivery-formatting pass, not a rewrite pass.
5. The existing ordered phone voice pipeline receives those `SpokenParts` and queues the complete story narration.
6. The selected story is marked told after the playback-formatting contract succeeds.

## Android Listen submenu

- **Thought** keeps the existing prepared/at-rest Listen behavior.
- **Spark** runs the 25-item weighted high-IU Spark path.
- **Constellation** selects and narrates the highest-Overall-IU unplayed Constellation Story.

The Constellation icon still cycles through a small set of decorative star patterns each time the submenu opens.
