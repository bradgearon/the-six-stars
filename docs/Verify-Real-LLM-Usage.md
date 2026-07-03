# How to Verify Real LLM is Being Used

This guide helps you confirm that Six Stars is actually sending text to your AI LLM and receiving real responses (not mock data).

## Quick Verification Checklist

### ? **1. Check Status Bar**

When you send a message, the status bar should show:

**Using REAL LLM:**
```
? Sending request to REAL AI LLM...
? Waiting for REAL LLM response...
? REAL LLM Response. Heart ? +2, Trust ? +1, Romance ? +2...
```

**Using MOCK Data:**
```
? Using MOCK data (no LLM endpoint configured)...
? MOCK DATA Response. Heart ? +2, Trust ? +1...
```

### ? **2. Check Debug Output Window**

In Visual Studio:
1. Go to **View ? Output**
2. Select **Debug** from the "Show output from:" dropdown
3. Look for these messages:

**Real LLM Active:**
```
[LLM] Configured endpoint: http://localhost:8080/v1/chat/completions
[LLM] Model: qwen2.5:7b-instruct
[OpenAiCompatibleChatModelClient] Sending POST to http://localhost:8080/v1/chat/completions
[OpenAiCompatibleChatModelClient] Response status: 200 OK
[OpenAiCompatibleChatModelClient] ? Success! Received 1234 characters
[StructuredStarAgent] ? Received LLM response (1234 chars)
[FallbackStarAgent] ? Primary LLM succeeded. Generated 456 characters.

=== DETERMINISTIC STATE CHANGE ===
Data Source: REAL LLM
Star: Elusiaa
Player Input: Elusiaa, tell me a story
Heart Delta: +2 (Suggested: +3)
Emotions Evaluated: 6
  joy: 0.7 (confidence: 0.85)
  trust: 0.8 (confidence: 0.90)
  ...
```

**Mock Data Active:**
```
[LLM] No endpoint configured. Using MockStarAgent.
[FallbackStarAgent] ? Primary LLM failed: ...
[FallbackStarAgent] Falling back to MockStarAgent...

=== DETERMINISTIC STATE CHANGE ===
Data Source: MOCK DATA
```

### ? **3. Check Debug JSON Panel**

The Debug JSON panel in the UI shows detailed information. Look for:

**Real LLM:**
```json
{
  "response": {
    "debug": {
      "notes": [
        "? StructuredStarAgent used REAL LLM (structured-output model).",
        "Emotions evaluated: 6 emotions",
        "Response length: 456 characters"
      ],
      "riskFlags": []  // Should be empty or not contain "llm_primary_failed"
    }
  }
}
```

**Mock Data:**
```json
{
  "response": {
    "debug": {
      "notes": [
        "? FallbackStarAgent used MOCK data after LLM failure: ...",
        "Configure LLM endpoint in appsettings.json to use real AI responses."
      ],
      "riskFlags": ["llm_primary_failed"]
    }
  }
}
```

### ? **4. Check Response Content**

**Real LLM responses:**
- Unique each time (even for same input)
- Contextually appropriate to your input
- Natural, varied language
- Specific references to what you said

**Mock responses:**
- Generic, template-like
- Same response for similar inputs
- May not directly address your input
- Predetermined patterns

## How to Enable Real LLM

### Option 1: Configure in appsettings.json

Edit `src/Wds.SixStars.Ui/appsettings.json`:

```json
{
  "LLM": {
    "EndpointUrl": "http://localhost:11434/v1/chat/completions",
    "Model": "qwen2.5:7b-instruct",
    "ApiKey": null,
    "UseJsonSchemaResponseFormat": true
  }
}
```

### Option 2: Use Environment Variables

```bash
# Windows (PowerShell)
$env:SIX_STARS_LLM_ENDPOINT="http://localhost:11434/v1/chat/completions"
$env:SIX_STARS_LLM_MODEL="qwen2.5:7b-instruct"

# Linux/Mac
export SIX_STARS_LLM_ENDPOINT="http://localhost:11434/v1/chat/completions"
export SIX_STARS_LLM_MODEL="qwen2.5:7b-instruct"
```

## Setup LLM Server (Ollama Example)

### Install Ollama

1. Download from https://ollama.ai
2. Install and run

### Pull a Model

```bash
ollama pull qwen2.5:7b-instruct
```

### Start the Server

```bash
ollama serve
```

Server runs on `http://localhost:11434` by default.

### Configure Six Stars

Update `appsettings.json`:
```json
{
  "LLM": {
    "EndpointUrl": "http://localhost:11434/v1/chat/completions",
    "Model": "qwen2.5:7b-instruct"
  }
}
```

## Troubleshooting

### Status shows "? Using MOCK data"

**Check:**
1. Is `appsettings.json` configured with an endpoint?
2. Is the LLM server running?
3. Can you reach it? `curl http://localhost:11434/v1/models`

### Status shows "? Sending" but then "? MOCK DATA Response"

This means the LLM request failed. Check Debug Output for the error:

```
[FallbackStarAgent] ? Primary LLM failed: Cannot connect to server
```

**Common Issues:**
- LLM server not running
- Wrong endpoint URL
- Firewall blocking connection
- Model not loaded in Ollama

### How to fix connection errors

1. **Verify LLM server is running:**
   ```bash
   curl http://localhost:11434/v1/models
   ```

2. **Check endpoint in Debug Output:**
   ```
   [LLM] Configured endpoint: http://localhost:8080/v1/chat/completions
   ```
   Does this match your LLM server?

3. **Test manually:**
   ```bash
   curl -X POST http://localhost:11434/v1/chat/completions \
     -H "Content-Type: application/json" \
     -d '{
       "model": "qwen2.5:7b-instruct",
       "messages": [{"role": "user", "content": "test"}]
     }'
   ```

4. **Check firewall:**
   - Windows: Allow port 11434
   - Linux: `sudo ufw allow 11434`

## Test Scenarios

### Test 1: Generic Input
Send: "Hello"

- **Real LLM:** Varied greeting, contextual to Star's personality
- **Mock:** Generic template response

### Test 2: Specific Question
Send: "Elusiaa, what did we talk about yesterday?"

- **Real LLM:** References actual conversation history, contextual response
- **Mock:** Generic response, may not address the question

### Test 3: Same Input Twice
Send the same message twice in a row.

- **Real LLM:** Different responses each time (creative variation)
- **Mock:** Identical or very similar responses

### Test 4: Emotions
Check Debug Output for emotion evaluation.

- **Real LLM:** All emotions evaluated (joy, trust, fear, etc.)
- **Mock:** May have incomplete emotion data

## Emotion Evaluation Verification

When using REAL LLM, you should see in Debug Output:

```
Emotions Evaluated: 6
  joy: 0.7 (confidence: 0.85)
  trust: 0.8 (confidence: 0.90)
  fear: 0.1 (confidence: 0.75)
  sadness: 0.2 (confidence: 0.80)
  affection: 0.9 (confidence: 0.95)
  excitement: 0.6 (confidence: 0.88)
```

All emotions from the Star's profile should be evaluated with scores and confidence levels.

## What Gets Sent to the LLM

The application sends:

1. **System Instructions** - Star's personality, lore, instructions
2. **Star Profile** - Traits, emotions, voice directives
3. **Player Input** - Your actual text or speech
4. **Game Context** - Area, quest, romance mode, flags
5. **Conversation History** - Recent interactions
6. **Current State** - Heart, Trust, Romance, Benaveil values
7. **Screen Context** - Recent screen captures (if enabled)

The LLM evaluates all of this and returns:

1. **Star's Response** - What the Star says
2. **Emotion Scores** - All emotions evaluated
3. **State Deltas** - Suggested relationship changes
4. **Memory Notes** - Important things to remember
5. **Relationship Events** - Significant moments
6. **Secrets Unlocked** - New lore revealed

## Summary

? **Verify Real LLM is Active:**

1. **Status Bar:** Shows "? REAL LLM" not "? MOCK DATA"
2. **Debug Output:** Shows successful LLM communication
3. **Debug JSON:** Contains "? StructuredStarAgent used REAL LLM"
4. **Response Quality:** Unique, contextual, natural
5. **Emotions:** All emotions evaluated with scores

? **If Seeing Mock Data:**

1. Configure `appsettings.json` with LLM endpoint
2. Start your LLM server (e.g., Ollama)
3. Verify connection with curl
4. Check Debug Output for errors
5. Restart Six Stars application

The application **does** send text to the real AI and **does** use the actual LLM response when properly configured. The mock is only a fallback for when no LLM is available!
