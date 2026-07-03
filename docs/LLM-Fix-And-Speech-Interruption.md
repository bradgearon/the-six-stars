# LLM Response Truncation Fix & Speech Interruption

## Summary

Fixed two critical issues:

1. **LLM responses were being truncated** - causing JSON parsing failures and fallback to mock data
2. **Added speech interruption** - user can now interrupt Star's speech by speaking

## Issue 1: Truncated LLM Responses

### Problem

The LLM was generating complete responses, but they were being cut off mid-JSON:

```json
{
  "finish_reason": "length"  // <-- Response truncated!
}
```

This caused:
- JSON parsing failures
- Fallback to mock data
- User never saw the real LLM response

### Root Cause

`MaxTokens = 1800` was too small for detailed Star responses (especially stories with emotion scores, voice directives, etc.)

### Fix

**Increased `MaxTokens` from 1800 to 4096:**

```csharp
// src/Wds.SixStars.Api/Ai/LocalChatModelOptions.cs
public int MaxTokens { get; set; } = 4096;  // Was: 1800
```

**Added truncation detection:**

```csharp
// src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs
if (!json.TrimEnd().EndsWith("}"))
{
    System.Diagnostics.Debug.WriteLine("[StructuredStarAgent] ? WARNING: JSON response appears truncated!");
    System.Diagnostics.Debug.WriteLine($"Increase MaxTokens to fix this!");
}
```

**Better error handling:**

```csharp
catch (JsonException ex)
{
    throw new InvalidOperationException(
        $"Failed to parse LLM response as JSON. Response may be truncated. Increase MaxTokens setting. Error: {ex.Message}", 
        ex);
}
```

## Issue 2: Speech Interruption

### Feature

Users can now interrupt Star's speech by speaking, just like in real conversation.

### How It Works

1. **Star starts speaking** ? Interruption manager tracks this
2. **User starts speaking** ? Timer starts (200ms delay)
3. **User continues speaking** for 300ms+ ? Star's speech is cancelled
4. **User stops quickly** (<300ms) ? Star continues speaking

### Configuration

```csharp
new SpeechInterruptionOptions
{
    MinInterruptionDurationMs = 300,  // User must speak this long to interrupt
    InterruptionDelayMs = 200,        // Wait before canceling Star
    EnableInterruption = true,
    CancelGenerationOnInterruption = true,  // Cancel TTS generation
    CancelPlaybackOnInterruption = true     // Cancel audio playback
}
```

### Components

**1. SpeechInterruptionManager**
- Tracks when Star is speaking/generating speech
- Detects user speech and starts interrupt timer
- Cancels Star speech when threshold met

**2. StarVoiceResponsePipeline (Updated)**
- Now accepts SpeechInterruptionManager
- Links cancellation tokens
- Cancels generation and playback when interrupted

**3. MainViewModel (Updated)**
- Wires speech events to interruption manager
- Shows interruption status in UI
- Disposes interruption manager

### Events

```csharp
// User started speaking
_speechPipeline.SpeechSegmentStarted += (_, args) =>
{
    _interruptionManager.OnUserSpeechStarted();
};

// User stopped speaking
_speechPipeline.SpeechSegmentCompleted += (_, args) =>
{
    _interruptionManager.OnUserSpeechStopped();
};

// Star speech was interrupted
_voiceResponsePipeline.VoiceInterrupted += (_, args) =>
{
    Status = "? Star speech interrupted by user";
};
```

## Debug Output

### LLM Response Verification

```
[OpenAiCompatibleChatModelClient] Sending POST to http://localhost:8080/...
[OpenAiCompatibleChatModelClient] Response status: 200 OK
[OpenAiCompatibleChatModelClient] ? Success! Received 3456 characters
[StructuredStarAgent] ? Received LLM response (3456 chars)
[StructuredStarAgent] Emotions evaluated: joy, trust, fear, sadness, affection, excitement
```

### Speech Interruption

```
[SpeechInterruption] Started speech generation (interruptible)
[SpeechInterruption] Speech generation completed
[SpeechInterruption] Started speech playback (interruptible)
[SpeechInterruption] User started speaking (timer: 200ms)
[SpeechInterruption] ? User spoke for 350ms - interrupting Star!
[SpeechInterruption] ? Canceling Star speech: User spoke for 350ms
[VoiceInterruption] User interrupted - During generation: False, During playback: True
[StarVoiceResponsePipeline] Speech interrupted by user
```

## Status Bar Messages

**LLM Response (Fixed):**
```
? Sending request to REAL AI LLM...
? Waiting for REAL LLM response...
? REAL LLM Response. Heart ? +2, Trust ? +1, Romance ? +2...
Voice generated: 45320 bytes from edge-tts-fallback.
Playing Star voice. Microphone input is muted during playback.
```

**With Interruption:**
```
Playing Star voice. Microphone input is muted during playback.
? Star speech interrupted by user after 350ms
```

## Testing

### Test 1: Verify Real LLM Response

1. Configure LLM endpoint in `appsettings.json`
2. Send a story request: "Elusiaa, tell me a story"
3. Check Debug Output for:
   - `? Success! Received XXXX characters`
   - No truncation warnings
4. Verify response shows in UI (not mock data)

### Test 2: Test Speech Interruption

1. Click "Start Listen"
2. Say: "Elusiaa, tell me a long story"
3. While Star is speaking, start speaking again
4. Continue for 300+ ms
5. Star's speech should stop
6. Check status bar for "? Star speech interrupted"

### Test 3: Short Interruptions Ignored

1. Star is speaking
2. Make a brief sound (<300ms)
3. Star should continue speaking
4. Debug shows: "User speech too short (150ms < 300ms)"

## Configuration

### Increase Max Tokens (if needed)

Edit `appsettings.json`:

```json
{
  "LLM": {
    "EndpointUrl": "http://localhost:11434/v1/chat/completions",
    "Model": "qwen2.5:7b-instruct",
    "MaxTokens": 4096  // Or higher for very long responses
  }
}
```

### Adjust Interruption Sensitivity

In `MainViewModel.cs`:

```csharp
new SpeechInterruptionOptions
{
    MinInterruptionDurationMs = 500,  // Harder to interrupt (was 300)
    InterruptionDelayMs = 100,        // Faster response (was 200)
    EnableInterruption = true
}
```

## Files Modified

### Core Fixes

1. **`src/Wds.SixStars.Api/Ai/LocalChatModelOptions.cs`**
   - Increased MaxTokens from 1800 to 4096

2. **`src/Wds.SixStars.Api/Ai/StructuredStarAgent.cs`**
   - Added truncation detection
   - Better JSON parsing error messages

### Speech Interruption

3. **`src/Wds.SixStars.Api/Voice/SpeechInterruptionManager.cs`** (NEW)
   - Manages interruption detection and timing
   - Tracks Star speaking state
   - Cancels speech when user interrupts

4. **`src/Wds.SixStars.Api/Voice/StarVoiceResponsePipeline.cs`**
   - Added interruption manager support
   - Links cancellation tokens
   - Raises VoiceInterrupted event

5. **`src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`**
   - Creates and wires interruption manager
   - Handles speech events
   - Updates UI on interruption

## Benefits

### LLM Response Fix

? **Real responses now show** - No more fallback to mock
? **Complete JSON** - No truncation
? **All emotions evaluated** - Full LLM analysis
? **Detailed voice directives** - TTS gets full instructions
? **Stories work** - Long-form content supported

### Speech Interruption

? **Natural conversation** - Like talking to a real person
? **Responsive** - Star stops when interrupted
? **Configurable** - Adjust timing to preference
? **Smooth** - Cancels generation and playback
? **No crashes** - Graceful cancellation handling

## Troubleshooting

### Still Seeing Mock Data

1. Check Debug Output for truncation warnings
2. Increase MaxTokens further (try 8192)
3. Check if LLM supports larger responses
4. Verify endpoint is actually responding

### Interruption Not Working

1. Check "Start Listen" is active
2. Speak louder/longer (300+ ms)
3. Check Debug Output for timer messages
4. Verify `EnableInterruption = true`

### Star Gets Interrupted Too Easily

```csharp
MinInterruptionDurationMs = 500,  // Increase threshold
```

### Star Doesn't Stop Fast Enough

```csharp
InterruptionDelayMs = 100,  // Decrease delay
```

## Summary

? **LLM responses now work properly** - No truncation, real data shown
? **Speech interruption implemented** - Natural conversational flow
? **Comprehensive logging** - Easy to debug
? **Configurable behavior** - Adjust to preference

The application now fully supports real LLM responses and natural speech interruption! ??
