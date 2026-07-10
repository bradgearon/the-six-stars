# Conversation History WebArchive Compile Fix

- Reworked the desktop conversation history website restore so it uses the existing Content/WebArchive template path instead of hardcoding the full page inside MainViewModel.
- Added Content/WebArchive/conversation-history.html with the placeholders the desktop command fills at runtime: {{generatedAt}}, {{sessionId}}, {{selectedStar}}, {{nav}}, and {{sections}}.
- Added Content/WebArchive to both API and UI project content-copy rules so the template is available from the normal Content root after build.
- Removed the C# raw-string HTML block that could cause compile errors in older/project-local compiler settings.
- Kept the Open Conversation History Website button and command wiring.
- Kept Android Cute Now and ActivateCuteModeNow bridge command wiring.
