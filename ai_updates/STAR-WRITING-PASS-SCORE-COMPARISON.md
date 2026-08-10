# Star writing pass score comparison

Additional writing passes now return `oldScore` and `newScore` with each proposed replacement.
Both are 0-100 scores using the same review standard.

The runtime applies a replacement only when `newScore > oldScore`.
A tie or lower new score keeps the existing words and writing.
Invalid scores or malformed replacements are ignored.
