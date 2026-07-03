# Real LLM Integration - Verification Update

## Summary

I've enhanced the Six Stars application to make it **crystal clear** when it's using **REAL AI LLM** vs **MOCK DATA**. The application was **already configured** to use real LLM when an endpoint is provided - I've just added extensive logging and status indicators to prove it.

## What Changed

### ? **Enhanced Status Messages**

**Before:**
```
Sending request to AI LLM...
Waiting for LLM response...
LLM Response Received. Heart ? +2...
```

**After (Real LLM):**
```
? Sending request to REAL AI LLM...
? Waiting for REAL LLM response...
? REAL LLM Response. Heart ? +2, Trust ? +1, Romance ? +2...
```

**After (Mock Data):**
```
? Using MOCK data (no LLM endpoint configured)...
? MOCK DATA Response. Heart ? +2...
```

### ? **Comprehensive Debug Logging**

Added detailed logging at every step of LLM communication:

```
[LLM] Configured endpoint: http://localhost:8080/v1/chat/completions
[LLM] Model: qwen2.5:7b-instruct
[OpenAiCompatibleChatModelClient] Sending POST to http://localhost:8080/v1/chat/completions
[OpenAiCompatibleChatModelClient] Model: qwen2.5:7b-instruct, Messages: 3, Temp: 0.7
[OpenAiCompatibleChatModelClient] Response status: 200 OK
[OpenAiCompatibleChatModelClient] ? Success! Received 1234 characters
[StructuredStarAgent] Sending request to LLM for Elusiaa...
[StructuredStarAgent] Player text: Elusiaa, tell me a story...
[StructuredStarAgent] ? Received LLM response (1234 chars)
[StructuredStarAgent] Emotions evaluated: joy, trust, fear, sadness, affection, excitement
[StructuredStarAgent] Suggested deltas - Heart:3 Trust:1 Romance:2
[FallbackStarAgent] Attempting to use primary LLM for Elusiaa...
[FallbackStarAgent] ? Primary LLM succeeded. Generated 456 characters.
```

### ? **Enhanced Debug Output**

The Debug Output window now shows:

```
=== DETERMINISTIC STATE CHANGE ===
Data Source: REAL LLM
Star: Elusiaa
Player Input: Elusiaa, tell me a story
Heart Delta: +2 (Suggested: +3)
Trust Delta: +1 (Suggested: +1)
Romance Delta: +2 (Suggested: +2)
Benaveil Delta: 0 (Suggested: 0)
Emotions Evaluated: 6
  joy: 0.7 (confidence: 0.85)
  trust: 0.8 (confidence: 0.90)
  fear: 0.1 (confidence: 0.75)
  sadness: 0.2 (confidence: 0.80)
  affection: 0.9 (confidence: 0.95)
  excitement: 0.6 (confidence: 0.88)

=== COMBINED SESSION TOTALS ===
...
================================
```

### ? **Enhanced Debug JSON**

The Debug JSON panel now clearly indicates data source:

```json
{
  "response": {
    "debug": {
      "notes": [
        "? StructuredStarAgent used REAL LLM (structured-output model).",
        "Emotions evaluated: 6 emotions",
        "Response length: 456 characters"
      ]
    },
    "emotionScores": {
      "joy": { "score": 0.7, "confidence": 0.85, "reason": "..." },
      "trust": { "score": 0.8, "confidence": 0.90, "reason": "..." },
      // ... all emotions
    }
  }
}
```

### ? **Fallback Indicators**

If LLM fails and falls back to mock:

```
[FallbackStarAgent] ? Primary LLM failed: Cannot connect to server
[FallbackStarAgent] Falling back to MockStarAgent...

Debug JSON:
{
  "debug": {
    "notes": [
      "? FallbackStarAgent used MOCK data after LLM failure: ...",
      "Configure LLM endpoint in appsettings.json to use real AI responses."
    ],
    "riskFlags": ["llm_primary_failed"]
  }
}
```

## How the Real LLM Works

### The Application Flow

1. **User enters text** ? "Elusiaa, tell me a story"

2. **Check configuration** ? Is LLM endpoint configured?
   - YES ? Use StructuredStarAgent with OpenAiCompatibleChatModelClient
   - NO ? Use MockStarAgent

3. **Build request payload:**
   ```json
   {
     "model": "qwen2.5:7b-instruct",
     "messages": [
       {"role": "system", "content": "Star personality and instructions..."},
       {"role": "system", "content": "Game context, history, state..."},
       {"role": "user", "content": "Elusiaa, tell me a story"}
     ],
     "temperature": 0.7,
     "max_tokens": 2048,
     "response_format": { "type": "json_object" }
   }
   ```

4. **Send HTTP POST** ? `http://localhost:8080/v1/chat/completions`

5. **LLM processes** and returns JSON:
   ```json
   {
     "starId": "Elusiaa",
     "spokenText": "Of course, dear friend. Let me tell you...",
     "emotionScores": {
       "joy": { "score": 0.7, "confidence": 0.85 },
       "trust": { "score": 0.8, "confidence": 0.90 },
       // ... all emotions
     },
     "suggestedStateDeltas": {
       "heart": 3,
       "trust": 1,
       "romance": 2
     },
     "memoryNotes": ["Player asked for a story"],
     "conversationSummary": "...",
     // ... more data
   }
   ```

6. **Parse response** ? Extract all fields

7. **Apply deterministic rules** ? Calculate actual state deltas (may differ from suggested)

8. **Update state** ? Save to state store

9. **Return to UI** ? Display response

10. **Generate voice** ? Send to TTS server

11. **Play audio** ? User hears Star's voice

### What Gets Sent to LLM

The application sends **YOUR ACTUAL TEXT** plus:

- ? Star's personality definition
- ? Star's emotion definitions  
- ? Star's canonical lore and backstory
- ? Current relationship state (Heart, Trust, Romance)
- ? Conversation history (last 18 interactions)
- ? Game context (area, quest, romance mode)
- ? Screen context (if screen awareness enabled)
- ? Player's exact input text

### What Gets Evaluated by LLM

The LLM evaluates and returns:

- ? **All emotions** defined in Star profile (typically 6-8 emotions)
- ? **Each emotion** gets score (0-1) and confidence (0-1)
- ? **Suggested state deltas** for Heart, Trust, Romance, Benaveil
- ? **Conversation summary** of what happened
- ? **Changes summary** of relationship shifts
- ? **Memory notes** to remember for later
- ? **Important player quotes** to preserve
- ? **Relationship events** that occurred
- ? **Secrets unlocked** (if any)
- ? **Star's spoken response** to the player

## Files Modified

### 1. `src/Wds.SixStars.Api/Ai/FallbackStarAgent.cs`
- Added debug logging for LLM attempts
- Clear indicators when using real LLM vs fallback
- Enhanced error messages with guidance

### 2. `src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`
- Debug logging for LLM requests and responses
- Emotion evaluation logging
- Confirmation that real LLM was used
- Response size and content indicators

### 3. `src/Wds.SixStars.Api/Ai/OpenAiCompatibleChatModelClient.cs`
- HTTP request logging (endpoint, model, message count)
- HTTP response logging (status code, body size)
- Detailed error messages for connection failures
- Success confirmation

### 4. `src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`
- Status bar shows REAL LLM vs MOCK DATA
- Pre-request check for LLM configuration
- Post-response verification of data source
- Enhanced debug output with emotion details
- Player input included in debug logs

### 5. `docs/Verify-Real-LLM-Usage.md` (NEW)
- Complete guide to verifying real LLM usage
- Troubleshooting connection issues
- Setup instructions for Ollama
- Test scenarios to prove real LLM is active

## How to Verify It's Working

### Option 1: Status Bar (Easiest)

Look at the status bar at bottom of window:

- **? REAL LLM Response** = Using real AI
- **? MOCK DATA Response** = Using mock fallback

### Option 2: Debug Output Window

In Visual Studio:
1. View ? Output
2. Show output from: Debug
3. Look for `[OpenAiCompatibleChatModelClient] ? Success!`

### Option 3: Debug JSON Panel

Check for:
```json
"notes": ["? StructuredStarAgent used REAL LLM"]
```

### Option 4: Response Quality

- **Real LLM:** Unique, contextual, natural every time
- **Mock:** Generic, templated, repetitive

## Setup for Real LLM

### Quick Setup with Ollama

```bash
# Install Ollama
# Download from https://ollama.ai

# Pull a model
ollama pull qwen2.5:7b-instruct

# Start server
ollama serve
```

### Configure Six Stars

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

### Run Six Stars

```bash
cd src/Wds.SixStars.Ui
dotnet run
```

### Test It

1. Type: "Elusiaa, tell me a story"
2. Click "Send"
3. Check status bar for "? REAL LLM Response"
4. Check Debug Output for LLM communication logs
5. See unique, contextual response

## Troubleshooting

### Shows "? MOCK DATA"

**Check Debug Output for:**
```
[LLM] No endpoint configured. Using MockStarAgent.
```

**Solution:** Configure LLM endpoint in `appsettings.json`

### Shows "? Sending" then "? MOCK DATA"

**Check Debug Output for:**
```
[FallbackStarAgent] ? Primary LLM failed: Cannot connect
```

**Solution:** 
1. Start LLM server: `ollama serve`
2. Verify connection: `curl http://localhost:11434/v1/models`
3. Check firewall settings

### HTTP Errors

**Debug Output shows:**
```
[OpenAiCompatibleChatModelClient] Response status: 404 Not Found
```

**Solutions:**
- Verify endpoint URL is correct
- Check model name is correct
- Make sure model is loaded: `ollama list`

## What You Get

? **The application DOES:**
- Send your actual text to the LLM
- Use the real LLM response (not mock)
- Evaluate all emotions with the LLM
- Process relationship state changes from LLM
- Show data from the actual response
- Generate unique responses every time

? **Clear Indicators:**
- Status bar shows "? REAL LLM" or "? MOCK DATA"
- Debug Output shows full HTTP communication
- Debug JSON shows which agent was used
- Error messages guide you to fix configuration

? **Fallback Safety:**
- If LLM fails, falls back to mock (app doesn't crash)
- Clearly indicates when fallback was used
- Provides helpful error messages

## Summary

The application **already worked** with real LLM - I've just made it **obvious** when it's using real AI vs mock data. Now you can:

1. **See clearly** if real LLM is active (status bar icons)
2. **Debug easily** with comprehensive logging
3. **Verify** all emotions are evaluated
4. **Confirm** actual player text is sent
5. **Track** full request/response flow

**The application sends text to the AI, evaluates emotions, and uses real responses when LLM is configured!** ??
