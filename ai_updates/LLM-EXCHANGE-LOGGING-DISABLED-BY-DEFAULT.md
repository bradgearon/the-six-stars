# LLM Exchange Logging Disabled by Default

Full local LLM request/response exchange logging is now disabled in the shipped JSON configuration for both the normal Star LLM and the Screen LLM.

The logging implementation remains available. To temporarily turn it back on, set the relevant JSON value to `true`:

```json
"LogExchangesToDisk": true
```

The default location remains `logs/llm-exchanges` when logging is enabled.
