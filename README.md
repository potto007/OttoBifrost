![OttoBifrost - The veil between worlds grows thin](https://raw.githubusercontent.com/potto007/OttoBifrost/main/docs/images/ottobifrost-title.png)

# OttoBifrost

### For Valheim 1.0.12

Walk up to a portal and you see through it. The disc on the portal shows the far
end live, from where your eyes are, and while you look the mod loads that place.
By the time you step through, the floors and walls over there already exist. A
trip between two built bases becomes a walk instead of a loading screen.

**Maintainer:** Paul Otto

--------------------

### What it does

- **A window in the portal.** Within 12 m of a connected wood or stone portal, a
  round preview shows the arrival side. It follows your head, so the view shifts
  as you move, and both faces of the portal show the same place. The disc is sized
  for the wood arch, so it looks small on the stone portal.
- **Loading before you arrive.** Within 15 m of a portal the zones around its
  destination start to load. Past 10 m that is 3x3 zones, inside 10 m it is 5x5,
  and inside 5 m it is 7x7. The area grows over a few seconds, and objects at the
  far end are created in small batches, so walking up does not hitch.
- **A room full of portals.** The portal nearest to you loads first and at full
  size. Every other destination loads 3x3, which is enough for its preview. A
  different portal only takes over as nearest once it is 2 m closer, so pacing
  around a hub room does not drag the load back and forth between bases.
- **Faster teleports.** When the destination is preloaded, the vanilla wait is
  skipped. The trip ends as soon as every object around the arrival point exists
  and there is a floor under you. If that is already true you walk straight
  through. If it is not, the screen dims while the rest arrives, and at the vanilla
  8 second mark vanilla takes over. Dungeon doors are left alone.

### What it costs

Only the closest preview renders live. The others keep their last frame. The live
one renders every frame inside 4 m, 10 times a second out to 8 m, and 3 times a
second beyond that, with no shadows and a 300 m draw distance. In my six-portal
hub room a preview render averaged 1.4 ms.

--------------------

The game writes `BepInEx/config/potto007.OttoBifrost.cfg` the first time it runs
with the mod loaded. When the server has the mod, the `OttoBifrost` section comes
from the server.

| Section | Key | Default | What it does |
| --- | --- | --- | --- |
| OttoBifrost | `LockConfiguration` | `true` | Only server admins can change the synced settings. |
| OttoBifrost | `PreloadDestinations` | `true` | Load the area around a portal's destination while you stand near the portal. Synced. |
| OttoBifrost | `FastTeleport` | `true` | Skip the vanilla wait when the destination is already loaded. Synced. |
| Debug | `LogPerformance` | `false` | Write a timing summary to the log every 5 seconds. Not synced. |

___________________________
#### Installation (manual)

Extract the DLL from the zip into `<GameDirectory>\BepInEx\plugins`, then start the
game.
___________________________
#### Installation (automatic)

Use Gale, r2modman or the Thunderstore Mod Manager. OttoBifrost is on Thunderstore
and on Hexium. Search for it and install.
___________________________

#### Servers

OttoBifrost works client only, and it works better with the mod on the server too.

A dedicated server sends each player only the objects near that player. Without the
mod on the server you can preload only bases you already visited this session. A
first visit shows bare terrain in the preview, the base builds itself around you
after you land, and every fast teleport waits for the arrival area to stop changing.

With the mod on the server, each player reports the portals they stand near, and the
server also sends the objects around each destination, after the player's own area.
It accepts at most 8 portals per player, and only portals within 30 m of that player,
so nobody can use it to scan the map. The server tells the player when a destination
has nothing left to send, and a trip to a complete destination is a walk-through.

When both sides have the mod, the server config wins and the versions must match. A
player on a different version cannot join.

### Version information

The release history lives in [CHANGELOG.md](Thunderstore/CHANGELOG.md), and it
renders on the Changelog tab of the Thunderstore package page.

## Credits

OttoBifrost is written and maintained by Paul Otto. It depends on the BepInEx pack,
and it carries ServerSync inside the DLL for the config sync and the version check.
Bugs are mine, so report them at https://github.com/potto007/OttoBifrost. MIT licence.
