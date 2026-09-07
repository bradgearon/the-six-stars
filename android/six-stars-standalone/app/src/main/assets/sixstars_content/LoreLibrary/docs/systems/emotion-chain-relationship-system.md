# Emotion Chain Relationship System

## Goal

The relationship system turns AI conversation into gameplay. The player does not simply choose dialogue options; they learn how each Star feels and how to move her heart over time.

## Emotion Categories

High-scoring emotional responses earn points. The recommended categories are:

| Category | Meaning |
|---|---|
| Happy | Joy, delight, simple positive reaction. |
| Sad | Sorrow, grief, recognition of pain. |
| Laugh | Amusement, playful surprise. |
| Angry | Moral anger or personal anger. |
| Hurt | Felt wounded by the player or situation. |
| Wondrous | Awe, amazement, mystery. |
| Lonely | Felt alone or reminded of isolation. |
| Frustrated | Blocked, irritated, impatient. |
| Comforted | Soothed by the player. |
| Vulnerable | Opened emotionally. |
| Impassioned | Inspired, stirred, moved to act. |
| Trusting | Feels safer with the player. |
| Concerned | Worried, cautious, protective. |
| Playful | Mischievous, teasing, lively. |
| Melancholy | Beautiful sadness, memory, soft grief. |
| Intrigued | Curious, drawn into mystery. |
| Protective | Wants to guard the player. |
| Hopeful | Feels the future may be better. |

## Scoring Rule

If the AI rates a response **8/10 or higher** in a category with sufficient confidence, award one point in that category for the current Star.

## Emotion Chains

Consecutive high scores form chains. Example:

> Laugh → Vulnerable → Wondrous → Vulnerable → Laugh

Positive chains create stronger heart movement than isolated points.

## Combo Examples

| Combo | Meaning | Reward |
|---|---|---|
| Comforted + Trusting + Hopeful | Healing bond. | Unlock personal memory. |
| Laugh + Playful + Vulnerable | Flirty/playful bond. | Cute banter or romance point. |
| Wondrous + Intrigued + Hopeful | Lore-awakening. | Secret clue or letter interpretation. |
| Concerned + Protective + Impassioned | Battle loyalty. | Combat assist line or buff. |
| Hurt + Angry + Frustrated | Relationship damage. | Dark-force audio cue chance. |

## Visual Feedback

- Star portrait briefly appears.
- Small heart/glow/shadow animation.
- Text such as “Elusiaa felt comforted.”
- Soft tone or light particle.
- During processing, a “listening” portrait animation.

## Love Feeling Threshold

When the right emotional categories and chains are built, the Star may enter a hidden **Love Feeling** state. This makes her open to being asked to become the player's girlfriend if Romance Mode is enabled.

The Love Feeling should never be one-dimensional. For Elusiaa it might require Trusting, Comforted, Hopeful, Vulnerable, and Playful. For Kaelia it might require Trusting, Intrigued, Vulnerable, Hopeful, and Wondrous.
