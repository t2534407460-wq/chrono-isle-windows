# Restore Island Music Mode Design

## Goal

Restore the existing collapsed-island music mode while keeping the legacy lyric controls removed from Settings.

## Scope

- Restore the `MediaAutoTakeover` Settings checkbox and its load/save binding.
- Restore `ToggleMusicMode` in the island context menu, including its persisted `IslandShowMusicMode` state.
- Start the existing media and spectrum services, restore the existing collapsed-media view and its top-dock behavior.
- Do not restore `LyricsEnabled`, `LyricsOffsetMs`, lyric settings controls, or their settings-window wiring.

## Verification

Static UI contracts must require the music setting, context action, media start path, and media-view branch. They must also forbid lyric Settings controls. The focused UI contract suite and application build must pass before publishing.
