# Screen LLM OpenAI-Compatible Vision Payload Fix

Updated the screen-awareness vision call so it sends screenshots in the standard OpenAI-compatible chat-completions format:

```json
{
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "..." },
        { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,..." } }
      ]
    }
  ]
}
```

Changes:

- Screen LLM now logs the payload shape, endpoint, model, image mime type, image size, and response-format mode without dumping the full base64 image.
- Screen LLM retries without `response_format` when a local OpenAI-compatible server rejects that parameter for image requests.
- The default ScreenLLM config now does not force `response_format` for vision calls; the prompt still requires compact JSON.
- 401 messages now mention the screen-specific token environment variable as well as the shared LM Studio token variables.

This keeps LM Studio/OpenAI-compatible VLM calls strict about the image shape while avoiding the common local-server parameter error caused by `response_format` on multimodal requests.
