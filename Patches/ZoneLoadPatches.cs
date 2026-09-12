using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OttoBifrost.Patches;

/// Makes the game treat the zones around each destination as part of the local active area,
/// so they load and stay alive.
[HarmonyPatch]
internal static class ZoneLoadPatches
{
    // Objects near a destination are created ahead of the vanilla distance sort, in small
    // batches, so the preview has something to show without a hitch.
    private const float PrimeInterval = 0.2f;
    // A pass that creates nothing doubles the wait up to this, so built destinations cost little.
    // New zones, objects or destinations bring the wait back to PrimeInterval.
    private const float MaxPrimeInterval = 2f;
    private const int CreatesPerPass = 20;
    // Under one zone wide, so the 3x3 zones around a destination hold every object in range.
    internal const float PrimeRadius = 40f;
    // The next few missing zones have their terrain built ahead on vanilla's heightmap thread, so
    // each spawns on its first poke instead of on the tick after its build. HeightmapBuilder keeps
    // only 16 finished builds and drops the oldest, which can be vanilla's own, so the queue stays
    // well under that.
    private const int TerrainLookahead = 4;
    private const int TerrainQueueLimit = 8;

    private static readonly List<ZDO> Candidates = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> CandidateSectors = new();
    private static readonly List<ZDO> NearObjects = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> NearSectors = new();
    private static readonly List<Vector2s> MissingZones = new();
    private static readonly Dictionary<Vector2s, float> TerrainRequestedAt = new();
    private static Heightmap? _zoneHeightmap;
    private static float _nextPrimeCheck;
    private static float _nextPrimeTime;
    private static float _primeWait = PrimeInterval;
    private static Destination[]? _primedDestinations;
    private static int _primedWorkKey;
    private static bool _inAreaReadyCheck;

    private static bool IsZoneLoaded(Vector2s zone)
    {
        return ZoneSystem.instance != null && ZoneSystem.instance.m_zones.ContainsKey(zone);
    }

    /// Every InActiveArea and OutsideActiveArea overload resolves through this method. The
    /// public wrappers are one-line forwarders the JIT may inline, so the patch sits here.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.PointInsideActiveArea))]
    private static void ZNetScenePointInsideActiveAreaPostfix(Vector3 point, ref bool __result)
    {
        if (!__result && Destinations.Any && Destinations.Covers(ZoneSystem.GetZone(point)))
            __result = true;
    }

    /// Puts destination objects in the near list, so ZNetScene creates them in its own type and
    /// distance order and RemoveObjects does not destroy them.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects))]
    private static void ZDOManFindSectorObjectsPostfix(ZDOMan __instance, List<ZDO> sectorObjects)
    {
        Destination[] destinations = Destinations.Active;
        if (destinations.Length == 0 || ZoneSystem.instance == null || _inAreaReadyCheck)
            return;

        long start = PerfStats.Begin();
        // Vanilla filled this set during the call. Reusing it skips the sectors it already added.
        HashSet<ZoneSystem.SectorIndex> visited = __instance.m_visitedSectorIndices;
        foreach (Destination destination in destinations)
        {
            for (int dy = -destination.Radius; dy <= destination.Radius; dy++)
            {
                for (int dx = -destination.Radius; dx <= destination.Radius; dx++)
                {
                    Vector2s zone = new(destination.Zone.x + dx, destination.Zone.y + dy);
                    if (IsZoneLoaded(zone))
                        __instance.FindObjects(zone, sectorObjects, visited);
                }
            }
        }

        PerfStats.SectorPatchTicks += PerfStats.Elapsed(start);
    }

    /// A sector marked visited here would contribute only its distant objects and block the full
    /// pass in ZDOManFindSectorObjectsPostfix. The zone test matches that pass, so each sector is
    /// handled by exactly one of the two.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.FindDistantObjects))]
    private static bool ZDOManFindDistantObjectsPrefix(Vector2s sector)
    {
        return _inAreaReadyCheck || !Destinations.Any || !Destinations.Covers(sector) || !IsZoneLoaded(sector);
    }

    /// Missing zones are taken in need order across all destinations: every centre zone, then
    /// every zone within PrimeRadius of a destination, which is what its first StaticView picture
    /// and a fast teleport wait for, then the remaining zones ring by ring.
    ///
    /// Vanilla calls this every 0.1 s. One destination zone spawns per call, and only when vanilla
    /// has nothing of its own to spawn.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.CreateLocalZones))]
    private static void ZoneSystemCreateLocalZonesPostfix(ZoneSystem __instance, ref bool __result)
    {
        Destination[] destinations = Destinations.Active;
        if (destinations.Length == 0)
            return;

        long start = PerfStats.Begin();
        int outerRing = 0;
        foreach (Destination destination in destinations)
            outerRing = Mathf.Max(outerRing, destination.Radius);

        MissingZones.Clear();
        AddMissingNearZones(__instance, destinations);
        for (int ring = 0; ring <= outerRing; ring++)
        {
            foreach (Destination destination in destinations)
            {
                if (destination.Radius < ring)
                    continue;

                for (int dy = -ring; dy <= ring; dy++)
                {
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != ring)
                            continue;

                        Vector2s zone = new(destination.Zone.x + dx, destination.Zone.y + dy);
                        // UpdateTTL unloads a zone 4 s after its last poke. Reset it on every
                        // tick, including ticks where vanilla is busy with the player's zones.
                        if (__instance.m_zones.TryGetValue(zone, out ZoneSystem.ZoneData data))
                            data.m_ttl = 0f;
                        else
                            AddMissing(zone);
                    }
                }
            }
        }

        // A true result means vanilla spawned one of its own zones this tick. Its next terrain
        // request comes on the next tick, and it must not queue behind builds for destinations.
        if (!__result && MissingZones.Count > 0)
        {
            Vector2s missing = MissingZones[0];
            NoteTerrainRequest(missing);
            // PokeLocalZone requests the terrain itself and spawns nothing until it is built.
            if (__instance.PokeLocalZone(missing))
            {
                __result = true;
                if (PerfStats.Enabled)
                {
                    PerfStats.ZonesSpawned++;
                    RecordTerrainWait(missing);
                }
            }

            RequestTerrainAhead(__instance);
        }

        PerfStats.ZonePatchTicks += PerfStats.Elapsed(start);
    }

    private static void AddMissingNearZones(ZoneSystem zoneSystem, Destination[] destinations)
    {
        foreach (Destination destination in destinations)
        {
            if (!zoneSystem.m_zones.ContainsKey(destination.Zone))
                AddMissing(destination.Zone);
        }

        foreach (Destination destination in destinations)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    Vector2s zone = new(destination.Zone.x + dx, destination.Zone.y + dy);
                    if (!zoneSystem.m_zones.ContainsKey(zone) && WithinPrimeRadius(zoneSystem, zone, destination.Position))
                        AddMissing(zone);
                }
            }
        }
    }

    private static void AddMissing(Vector2s zone)
    {
        if (MissingZones.Count <= TerrainLookahead && !MissingZones.Contains(zone))
            MissingZones.Add(zone);
    }

    /// Requests terrain for the zones after the first in MissingZones, which PokeLocalZone has
    /// already requested.
    private static void RequestTerrainAhead(ZoneSystem zoneSystem)
    {
        HeightmapBuilder builder = HeightmapBuilder.instance;
        if (builder == null || WorldGenerator.instance == null || MissingZones.Count < 2)
            return;
        if (_zoneHeightmap == null)
            _zoneHeightmap = zoneSystem.m_zonePrefab.GetComponentInChildren<Heightmap>();
        if (_zoneHeightmap == null)
            return;

        // The lock is re-entrant, and holding it keeps the count right while builds are added.
        lock (builder.m_lock)
        {
            if (PerfStats.Enabled && builder.m_toBuild.Count > PerfStats.TerrainQueueMax)
                PerfStats.TerrainQueueMax = builder.m_toBuild.Count;

            int room = TerrainQueueLimit - builder.m_toBuild.Count - builder.m_ready.Count;
            for (int i = 1; i < MissingZones.Count && room > 0; i++)
            {
                Vector2s zone = MissingZones[i];
                // Queues a build only when the zone has none queued or finished.
                if (builder.IsTerrainReady(ZoneSystem.GetZonePos(zone), _zoneHeightmap.m_width, _zoneHeightmap.m_scale,
                        _zoneHeightmap.IsDistantLod, WorldGenerator.instance))
                    continue;
                room--;
                NoteTerrainRequest(zone);
            }
        }
    }

    private static void NoteTerrainRequest(Vector2s zone)
    {
        if (!PerfStats.Enabled || TerrainRequestedAt.ContainsKey(zone))
            return;
        // Zones that stop being destinations never spawn, so the map is bounded here.
        if (TerrainRequestedAt.Count >= 64)
            TerrainRequestedAt.Clear();
        TerrainRequestedAt[zone] = Time.time;
    }

    private static void RecordTerrainWait(Vector2s zone)
    {
        if (!TerrainRequestedAt.TryGetValue(zone, out float requestedAt))
            return;
        TerrainRequestedAt.Remove(zone);
        PerfStats.AddZoneWait(Time.time - requestedAt);
    }

    /// Whether any part of the zone lies within PrimeRadius of point, on the ground plane.
    private static bool WithinPrimeRadius(ZoneSystem zoneSystem, Vector2s zone, Vector3 point)
    {
        float halfZone = zoneSystem.m_zoneSize * 0.5f;
        Vector3 zonePos = ZoneSystem.GetZonePos(zone);
        float gapX = Mathf.Max(Mathf.Abs(point.x - zonePos.x) - halfZone, 0f);
        float gapZ = Mathf.Max(Mathf.Abs(point.z - zonePos.z) - halfZone, 0f);
        return gapX * gapX + gapZ * gapZ <= PrimeRadius * PrimeRadius;
    }

    /// IsAreaReady finds its objects through FindSectorObjects. With the destination patches
    /// active, it would wait for every destination, not only the point it checks.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    private static void ZNetSceneIsAreaReadyPrefix()
    {
        _inAreaReadyCheck = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    private static void ZNetSceneIsAreaReadyFinalizer()
    {
        _inAreaReadyCheck = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))]
    private static void ZNetSceneCreateDestroyObjectsPostfix(ZNetScene __instance)
    {
        Destination[] destinations = Destinations.Active;
        ZoneSystem zoneSystem = ZoneSystem.instance;
        if (destinations.Length == 0 || ZDOMan.instance == null || zoneSystem == null || Time.time < _nextPrimeCheck)
            return;
        _nextPrimeCheck = Time.time + PrimeInterval;

        int workKey = WorkKey(destinations, zoneSystem);
        if (destinations != _primedDestinations || workKey != _primedWorkKey)
        {
            _primedDestinations = destinations;
            _primedWorkKey = workKey;
            _primeWait = PrimeInterval;
        }
        else if (Time.time < _nextPrimeTime)
        {
            return;
        }

        long start = PerfStats.Begin();
        int budget = CreatesPerPass;
        CandidateSectors.Clear();
        foreach (Destination destination in destinations)
        {
            for (int dy = -1; dy <= 1 && budget > 0; dy++)
            {
                for (int dx = -1; dx <= 1 && budget > 0; dx++)
                {
                    Vector2s zone = new(destination.Zone.x + dx, destination.Zone.y + dy);
                    if (!zoneSystem.m_zones.ContainsKey(zone))
                        continue;

                    Candidates.Clear();
                    ZDOMan.instance.FindObjects(zone, Candidates, CandidateSectors);
                    CreateInTypeOrder(__instance, zoneSystem, zone, destination.Position, ref budget);
                }
            }

            if (budget == 0)
                break;
        }

        Candidates.Clear();
        _primeWait = budget < CreatesPerPass ? PrimeInterval : Mathf.Min(_primeWait * 2f, MaxPrimeInterval);
        _nextPrimeTime = Time.time + _primeWait;
        if (PerfStats.Enabled)
        {
            PerfStats.ForcedCreates += CreatesPerPass - budget;
            PerfStats.CreatePatchTicks += PerfStats.Elapsed(start);
        }
    }

    /// Follows the vanilla type order so no piece appears before the terrain edits and supports
    /// under it. Portals go first within a type so a preview finds its far portal early.
    private static void CreateInTypeOrder(ZNetScene scene, ZoneSystem zoneSystem, Vector2s zone, Vector3 center, ref int budget)
    {
        float radiusSqr = PrimeRadius * PrimeRadius;
        for (int type = (int)ZDO.ObjectType.Terrain; type >= (int)ZDO.ObjectType.Default; type--)
        {
            ZDO.ObjectType objectType = (ZDO.ObjectType)type;
            if (!zoneSystem.IsZoneReadyForType(zone, objectType))
                return;

            foreach (bool portalsOnly in PortalPasses)
            {
                foreach (ZDO zdo in Candidates)
                {
                    if (zdo.Type != objectType || !zdo.IsValid() || scene.m_instances.ContainsKey(zdo))
                        continue;
                    if (Portals.IsPortal(zdo) != portalsOnly)
                        continue;
                    // A terrain edit shapes its whole zone, so its distance does not matter.
                    if (objectType != ZDO.ObjectType.Terrain && (zdo.GetPosition() - center).sqrMagnitude > radiusSqr)
                        continue;

                    // Lower types must wait while this type is unfinished.
                    if (budget == 0)
                        return;

                    try
                    {
                        if (scene.CreateObject(zdo) != null)
                            budget--;
                    }
                    catch (Exception ex)
                    {
                        OttoBifrostPlugin.Log.LogDebug($"Could not create {zdo.m_uid} near a destination: {ex.Message}");
                    }
                }
            }
        }
    }

    /// Changes when a destination's zones load or its known objects change, which is when a pass
    /// can find something new to create. Costs a few dictionary lookups per destination.
    private static int WorkKey(Destination[] destinations, ZoneSystem zoneSystem)
    {
        int key = destinations.Length;
        foreach (Destination destination in destinations)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    bool loaded = zoneSystem.m_zones.ContainsKey(new Vector2s(destination.Zone.x + dx, destination.Zone.y + dy));
                    key = key * 31 + (loaded ? 1 : 0);
                }
            }

            key = key * 31 + DestinationSync.CountKnownObjects(destination.Position);
        }

        return key;
    }

    /// Every object within PrimeRadius of origin exists, apart from those more than
    /// behindAllowance behind it along forward. These are the objects the destination pass
    /// creates first, and vanilla creates the rest of a zone nearest the player first, so at a
    /// big base this passes well before IsAreaReady, which waits for the whole 3x3 zones.
    internal static bool AreNearObjectsBuilt(Vector3 origin, Vector3 forward, float behindAllowance)
    {
        ZoneSystem zoneSystem = ZoneSystem.instance;
        ZNetScene scene = ZNetScene.instance;
        if (zoneSystem == null || scene == null || ZDOMan.instance == null)
            return false;

        float radiusSqr = PrimeRadius * PrimeRadius;
        Vector2s centre = ZoneSystem.GetZone(origin);

        NearObjects.Clear();
        NearSectors.Clear();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                Vector2s zone = new(centre.x + dx, centre.y + dy);
                if (!WithinPrimeRadius(zoneSystem, zone, origin))
                    continue;
                // An unloaded zone has no ground yet.
                if (!zoneSystem.m_zones.ContainsKey(zone))
                    return false;
                ZDOMan.instance.FindObjects(zone, NearObjects, NearSectors);
            }
        }

        bool built = true;
        foreach (ZDO zdo in NearObjects)
        {
            if (!zdo.IsValid() || !scene.IsPrefabZDOValid(zdo) || scene.HaveInstance(zdo))
                continue;

            // A terrain edit shapes its whole zone, so its position does not matter.
            Vector3 offset = zdo.GetPosition() - origin;
            offset.y = 0f;
            if (zdo.Type != ZDO.ObjectType.Terrain &&
                (offset.sqrMagnitude > radiusSqr || Vector3.Dot(offset, forward) < -behindAllowance))
                continue;

            built = false;
            break;
        }

        NearObjects.Clear();
        return built;
    }

    private static readonly bool[] PortalPasses = { true, false };
}
