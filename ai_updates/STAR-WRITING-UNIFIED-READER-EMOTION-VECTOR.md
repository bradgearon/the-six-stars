# Unified compact reader-emotion IU vector

The writing shelf now reviews both `spark` and `constellation-story` output with the same `reader-emotion-iu-v1` discipline.

The local LLM never returns an array of `{ emotion, value }` objects for writing review. It returns a compact semicolon-delimited positional string. The canonical vector is 31 values: the 30 reader emotions followed by Overall. Spark batch review keeps the efficient outer shape `{"0":"...","1":"...","2":"...","3":"...","4":"..."}`; Constellation review uses `{"valueVector":"..."}`.

The vector order is:

`Mirthful,Playful,Delighted,Intrigued,Engrossed,Surprised,Wondrous,Awed,Admiring,Moved,Tender,Comforted,Hopeful,Inspired,Impassioned,Exhilarated,Connected,Affectionate,Attracted,Yearning,Vulnerable,Melancholy,Sad,Haunted,Uneasy,Afraid,Concerned,Angry,Determined,Contemplative,Overall`

All values are open-ended information units. 100 IU is a calibration reference, not a maximum.

Star persistence rules for Sparks were moved to the same reader-emotion category vocabulary already used by Constellations. The old 27 literary vector and the short-lived named Constellation emotion array remain migration-readable only.
