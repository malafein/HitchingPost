# Changelog

## 1.0.8
* Fixed tether ropes appearing at the wrong location (or not at all) after reloading a world, caused by the saved rope endpoint going stale across sessions (issue #7).
* Improved hitching ropes not appearing on dedicated servers (issue #6, not yet fully confirmed). Network ownership of the creature is now claimed before the rope is created so the endpoint syncs, and a creature in hitching mode is flagged so nearby players all see the rope — not just the player doing the hitching.
* Removed the straight-line fallback rope; the authentic vfx_Harpooned rope is now the only rope.
* A stale tether to a beam that no longer exists is now cleared automatically instead of retrying indefinitely.
* Minor performance and robustness cleanups.

## 1.0.6
* Fixed TetherController being attached to wild/untamed creatures, which caused log spam (issue #5).
* Creature names in log output now display their localized name instead of the raw key (e.g. "Boar" instead of "$enemy_boar").
* Verbose internal log messages are now gated behind the DebugMode config option.

## 1.0.5
* Fixed NullReferenceException spam in the console when creating a rope (issue #4).

## 1.0.4
* Fixed hitching of non-tamed creatures. Previously, player was able to hitch any tameable creature. This was not intended and has been fixed. Now, the interaction only works on allied tameable creatures.

## 1.0.3
* Updated README and fixed some Debug text.

## 1.0.2
* Fixed duplicate ropes appearing in multiplayer.

## 1.0.1
* Allowed hitching posts to have multiple tamed creatures hitched to them simultaneously.
* Fixed an issue where creatures remained hitched (preserving the active hovering prompt) when their respective post/beam was dismantled.

## 1.0.0
- Initial release
