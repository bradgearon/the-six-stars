# Android Activity Result Forward-Reference Fix

## Problem

`recordAudioPermissionLauncher` is initialized as an instance field and its callback checks `recording`. Java reported an illegal forward reference because `recording` was declared later in the class.

## Fix

Moved the existing `recording` field declaration above the Activity Result launcher initializers. Runtime behavior is unchanged; the callback still starts recording only after microphone permission is granted and only when a recording is not already active.
