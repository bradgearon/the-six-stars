# Background memory review scheduling and trace

Normal foreground Chat/Photo turns now treat the primary emotion/memory review as part of the turn transaction:

1. Gemma streams speech normally; the first complete sentence may already be synthesizing/playing through Qwen. Once a real Android foreground owns the mobile lane, a later Talk request waits for that lane instead of cancelling the current Gemma token before EOS.
2. At complete Gemma response (EOS), the runtime immediately queues `PrimaryBackgroundAnalysis`.
3. That primary request uses the dispatcher's critical priority, bypasses PlayerActivity suppression, and cannot be cancelled by foreground preemption.
4. Before any next foreground turn acquires a foreground dispatcher lease, it waits for all prior primary reviews to finish their durable memory/state commit.
5. Optional information follow-ups, interstar work, story generation, daily state, writing, and other background stacks remain cancellable/preemptible.

This removes the old Android AudioTrack-playback acknowledgement as a prerequisite for normal Chat/Photo memory review scheduling. It also removes the pre-gate `CancelAllRuntimeOperations` race where tapping Talk after sentence one could cancel the still-streaming previous Gemma request before that request reached EOS and created its primary review. Explicit cancel/stop remains available, and ordinary background/interstar/writing work remains preemptible. Generated story/girlfriend-proposal narration keeps the explicit speech-path release compatibility hook.

## Trace configuration

In `appsettings.json` or `appsettings.Local.json`:

```json
"BackgroundAnalysisTracing": {
  "Enabled": true,
  "LogFilePath": "logs/background-analysis-trace.log"
}
```

Environment overrides are also available:

- `SIX_STARS_BACKGROUND_ANALYSIS_TRACE=true`
- `SIX_STARS_BACKGROUND_ANALYSIS_TRACE_FILE=logs/background-analysis-trace.log`

Useful trace phases include:

- `host.mobile.foreground-gate-wait-start`
- `host.mobile.foreground-gate-acquired`
- `host.mobile.first-speech-request-queued`
- `host.mobile.gemma-eos`
- `pending.stored`
- `barrier.registered`
- `release.queue`
- `primary.enqueued`
- `dispatcher.Started`
- `primary.llm-start`
- `primary.llm-complete`
- `primary.commit-start`
- `primary.commit-complete`
- `barrier.settled`
- `foreground.wait-start`
- `foreground.wait-complete`
- `dispatcher.PreemptionSkippedProtected`
- `primary.cancelled-unexpected`

If the bug recurs, the trace should make it clear whether the review was queued late, waited behind a foreground lease, was unexpectedly cancelled, spent time in the LLM call, or spent time committing state.
