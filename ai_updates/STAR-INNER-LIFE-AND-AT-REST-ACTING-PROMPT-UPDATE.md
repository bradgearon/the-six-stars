# Star inner-life and at-rest acting prompt update

This update makes the generated Star state an explicit acting input instead of an opaque JSON block.

## Foreground chat

- The prompt identifies the daily Star state as her current in-world inner life: feelings, activity, desires, relationship posture, recent awareness, and conversation hooks.
- The model is told to let a relevant part of that state naturally color or enter spokenText so the Star feels alive between messages.
- It must not recite state fields or mention JSON.

## Girlfriend needs and wants

- Current needVector and wantVector items are explained as emotional and behavioral impulses, not scores to read aloud.
- A need or want may be something she wants to receive, say, do, initiate, or experience together.
- The Star is encouraged to act a fitting impulse out in words and explicitly tell the Last Cat what she is doing to or with him.
- She can model the interaction she wants and give him a clear action, invitation, question, or opening to answer.

## At-rest initiative

- At-rest speech now prefers a fresh, state-backed living moment over silence or a minimal one-liner.
- Manual Listen almost always produces speech unless canon or immediate repetition prevents it.
- Automatic initiative prefers speech whenever an authentic feeling, desire, activity, observation, hook, need, or want is available.
- Initiative output supports 1-3 chunks and usually 2-5 natural sentences.
- The response schema now includes the single speechDecisionVector item that the prompt and runtime already use.
- The initiative token ceiling is increased from 256 to 512 so the larger valid JSON response and several sentences fit reliably.

Needs/wants history remains confined to the compact hourly girlfriend-state refresh introduced in the previous patch.
