# LLM Daily Log Update

The runtime appends local LLM request/response/debug entries to one plain `.log` file per local day.

Default location:

```text
logs/llm-exchanges/YYYY-MM-DD.log
```

Each block includes the request id, kind, debug label, response-format mode, and the full formatted request or response text.
