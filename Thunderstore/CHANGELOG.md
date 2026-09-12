# Changelog

All notable changes to OttoBifrost.

## v1.1.0

- New `PreviewMode` setting in the `Preview` section. Not synced.
- `LiveView`, the default, keeps the continuous, head-tracking preview from v1.0.0.
- `StaticView` takes one picture of the destination once the objects around the
  arrival point are built, and renders nothing after that. Each approach takes a
  new picture.

## v1.0.0

- First release, for Valheim 1.0.12 and BepInEx 5.4.2350.
- Live preview of the far end on wood and stone portals, from your eye position,
  on both sides of the portal.
- The destination starts to load within 15 m of a portal, and the loaded area grows
  as you get closer.
- The nearest portal loads first and at full size. Other destinations load 3x3,
  enough for their previews. The nearest only changes when another portal is 2 m
  closer.
- Zones load ring by ring across all destinations and stay loaded while you stand
  near. Objects come up in vanilla type order, terrain edits first, so nothing
  appears before the ground under it.
- Faster teleports to a preloaded destination. The trip ends once the arrival area
  is built and a floor is found, and vanilla takes over at the 8 second mark.
  Dungeon doors keep vanilla behaviour.
- With the mod on a dedicated server, the server also sends the objects around each
  destination you stand near and reports when one is complete. At most 8 per player,
  and only for portals within 30 m of that player.
- Only the nearest preview renders live, at a rate that drops with distance, without
  shadows and with a 300 m draw distance.
- `LogPerformance` writes a timing summary every 5 seconds. Off by default.
