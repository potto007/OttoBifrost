# Changelog

All notable changes to OttoBifrost.

## Unreleased

- `PreviewMode = None` turns the portal preview off, so another mod can draw its own
  visual in the opening. Destinations still preload and fast teleports still apply.
  The idea comes from ItsBlade's fork of OttoBifrost.

## 1.3.3

- Rebuilt against Valheim 1.0.16. Every Harmony patch target still resolves in this build, and nothing else changed.

## v1.3.2

- Rebuilt against Valheim 1.0.15. Every Harmony patch target still resolves in this build, and nothing else changed.

## v1.3.1

- Rebuilt against Valheim 1.0.14. Every Harmony patch target still resolves in this build, and nothing else changed.

## v1.3.0

- `StaticView` shows its first picture sooner. It waits only for the objects within
  40 m of the far portal, on the side you arrive on, instead of every object in the
  3x3 zones around it. Height counts toward the 40 m, so the interior of a crypt
  next to the portal, which the game places far above its entrance, doesn't hold
  the picture back. A second picture replaces the first once the whole arrival area
  has loaded. Each picture waits 0.2 s, down from 0.5 s.
- Fast teleports end once the objects within 40 m of the arrival point exist. They
  used to wait for the whole 3x3 zones, so a portal taken a few seconds after
  landing could hold you behind the loading screen for 1-3 s.
- For 5 s after you land, the portal nearest you still loads first and at full
  size. Before, every portal waited behind the area you landed in.
- Destination zones load in the order pictures and teleports need them. Each
  destination gets its centre zone first, then the zones within 40 m of its far
  portal, and only then its outer zones. In a hub with 8 portals, first pictures
  used to trickle in over about 16 s.
- Destination zones load up to twice as fast. The mod asked the game's terrain
  thread for one zone at a time, so every zone sat out an extra 0.1 s tick. It now
  queues terrain for the next 4 zones, but only while the game has no zones of its
  own to load.
- Objects near a destination come up in a fixed order. The game's own rule still
  holds: terrain edits, then building pieces, then everything else. Within each of
  those, portals go first, then objects in front of the far portal, closest first.
  Before, each zone's objects came in whatever order the game stored them.
- `LogPerformance` reports more. Each 5 s summary adds how long the next destination
  zone waited to spawn and how busy the terrain thread was. Each `StaticView`
  picture logs how long after you came in range it was taken, when the far portal,
  the nearby zones and the nearby objects were ready, and the last object it waited
  for.

## v1.2.0

- `StaticView` is now the default `PreviewMode`. v1.1.0 wrote `LiveView` into every
  config file the first time it ran, so a saved `LiveView` says nothing about what
  you chose. The first time v1.2.0 loads a config file saved by an older version, it
  switches `LiveView` to `StaticView` and logs the change. After that it leaves the
  setting alone.
- `StaticView` pictures shimmer and ripple, fade out toward the rim, and let 30% of
  the portal behind them show through.
- `StaticView` no longer takes a picture mid-teleport, when the far side is the area
  you are leaving.
- Fast teleports keep their own clock. In debug mode, Server devcommands' "Debug mode
  fast teleport" sets the teleport timer to 15. The fast path read that as past its
  8 second limit and handed the trip to vanilla on the first frame, so you could land
  before the floors arrived.
- Standing near built destinations costs less. The pass that creates their objects
  ran every 0.2 s even when nothing was left to create. Now each empty pass doubles
  the wait, up to 2 s, and any change to those zones or their objects brings it back
  to 0.2 s.
- With `LogPerformance` on, the log records "server reports complete" once per
  change. The server repeats every state whenever your list of nearby portals
  changes, and each repeat used to get its own line.

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
  near them. Objects come up in vanilla type order, terrain edits first, so nothing
  appears before the ground under it.
- Faster teleports to a preloaded destination. The trip ends once every object
  around the arrival point exists and there is a floor under you, and vanilla takes
  over at the 8 second mark. Dungeon doors keep vanilla behaviour.
- With the mod on a dedicated server, the server also sends the objects around each
  destination you stand near and reports when one is complete. At most 8 per player,
  and only for portals within 30 m of that player.
- Only the nearest preview renders live, at a rate that drops with distance, without
  shadows and with a 300 m draw distance.
- `LogPerformance` writes a timing summary every 5 seconds. Off by default.
