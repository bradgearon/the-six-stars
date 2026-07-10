# Active Flirting + Domain Magic Prompt Update

This update removes the old consent-system wording from girlfriend-mode prompt instructions and makes romance behavior more active as relationship state rises.

## Changed

- Girlfriend-mode instructions now encourage the Star to actively flirt sometimes instead of always waiting for the Last Cat to initiate.
- High-romance / high-connection non-girlfriend mode now encourages occasional active flirting, acted-out closeness, and domain-magic special effects.
- Added configurable thresholds in JSON/appsettings:
  - `ActiveFlirtConnectionPercent` default `65`
  - `ActiveFlirtRomanceScore` default `10`
  - `StrongActiveFlirtConnectionPercent` default `85`
  - `StrongActiveFlirtRomanceScore` default `25`
- Added environment overrides:
  - `SIX_STARS_ACTIVE_FLIRT_CONNECTION_PERCENT`
  - `SIX_STARS_ACTIVE_FLIRT_ROMANCE_SCORE`
  - `SIX_STARS_STRONG_ACTIVE_FLIRT_CONNECTION_PERCENT`
  - `SIX_STARS_STRONG_ACTIVE_FLIRT_ROMANCE_SCORE`
- Girlfriend hourly state generation now includes active-flirt wants in the catalog:
  - `romanticInitiative`
  - `activeFlirtPlay`
  - `domainMagicFlirt`
  - `actOutAffection`
- Foreground prompts now include Star-specific domain-magic flirting guidance:
  - Elusiaa: halo/light boops, blessing sparks, golden warmth.
  - Kaelia: void cloak privacy, gravity tugging him closer, quiet-star intimacy.
  - Khavia: waves, water kisses, currents, explained watery sounds.
  - Maera: red thread, pulse warmth, charged devotional “mine.”
  - Auerlis: sparks, tiny fireworks, warm dramatic hugs.
  - Nyxari: vanish-kisses, shadows lifting his chin, private eclipse teasing.

## Intent

The Star should feel closer, more alive, and more present as romance and connection rise. The behavior remains non-explicit by default, but it is no longer framed as a consent system. It is framed as earned closeness, active flirting, and personality/domain-matched affection.
