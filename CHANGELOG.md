# Changelog

## 1.0.6
* Fixed log spam caused by TetherController being attached to wild/untamed creatures (issue #5).
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
