# Changelog

All notable changes to OttoBifrost.

## Unreleased

- `StaticView` shows a picture sooner at a big base. The first picture waits only
  for the objects within 40 m in front of the far portal, not every object in the
  3x3 zones around it. A second picture replaces it once the whole arrival area has
  loaded. The delay before each picture dropped from 0.5 s to 0.2 s.
- With `LogPerformance` on, each `StaticView` picture logs how long after you came in
  range it was taken, and whether it covers the near objects or the whole area.
- Fast teleports end once the objects within 40 m of the arrival point exist, not
  every object in the 3x3 zones around it. Taking a portal a few seconds after
  landing used to wait 1-3 s behind the loading screen while the far edges of the
  base built.
- For 5 s after you land, the portal nearest you keeps loading first and at full
  size. Before, every portal waited behind the area you landed in, so a portal you
  took right away was not preloaded yet.
- Destination zones load in need order. Every destination gets its centre zone, then
  every zone within 40 m of its far portal, before any destination gets its outer
  zones. In a hub with many portals, the first pictures used to come out one by one
  over about 16 s while the outer zones of earlier portals loaded first.
- Destination zones load up to twice as fast. The mod asked the game's terrain thread
  for one zone at a time, so every zone waited a full 0.1 s tick for its terrain. It
  now queues terrain for the next 4 zones ahead, and only while the game has none of
  its own zones to spawn.
- With `LogPerformance` on, the timing summary shows how long destination zones waited
  from terrain request to spawn, and the longest terrain build queue.

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
