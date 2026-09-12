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

    // Objects further than this behind a far portal are out of a StaticView picture, so they are
    // created after the ones in front.
    internal const float BehindAllowance = 2f;

    private static readonly List<ZDO> Candidates = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> CandidateSectors = new();
    private static readonly List<Candidate> Queue = new();
    private static readonly Dictionary<Vector2s, ZDO.ObjectType> UnfinishedZones = new();
    private static readonly List<ZDO> NearObjects = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> NearSectors = new();
    private static readonly List<Vector2s> MissingZones = new();
    // The zone at the front of the need order, and when it got there, for the perf summary.
    private static Vector2s _frontZone;
    private static float _frontSince = -1f;
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

        if (MissingZones.Count == 0)
            _frontSince = -1f;
        else if (_frontSince < 0f || !(MissingZones[0] == _frontZone))
        {
            _frontZone = MissingZones[0];
            _frontSince = Time.time;
        }

        // A true result means vanilla spawned one of its own zones this tick. Its next terrain
        // request comes on the next tick, and it must not queue behind builds for destinations.
        if (!__result && MissingZones.Count > 0)
        {
            // PokeLocalZone requests the terrain itself and spawns nothing until it is built.
            if (__instance.PokeLocalZone(MissingZones[0]))
            {
                __result = true;
                if (PerfStats.Enabled)
                {
                    PerfStats.ZonesSpawned++;
                    PerfStats.AddZoneWait(Time.time - _frontSince);
                }
                _frontSince = -1f;
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
            }
        }
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
        // Destinations are in load order, so the portal the player is nearest gets the budget first.
        foreach (Destination destination in destinations)
        {
            CreateNearDestination(__instance, zoneSystem, destination, ref budget);
            if (budget == 0)
                break;
        }

        _primeWait = budget < CreatesPerPass ? PrimeInterval : Mathf.Min(_primeWait * 2f, MaxPrimeInterval);
        _nextPrimeTime = Time.time + _primeWait;
        if (PerfStats.Enabled)
        {
            PerfStats.ForcedCreates += CreatesPerPass - budget;
            PerfStats.CreatePatchTicks += PerfStats.Elapsed(start);
        }
    }

    private readonly struct Candidate(ZDO zdo, bool portal, bool behind, float distanceSqr)
    {
        public readonly ZDO Zdo = zdo;
        public readonly bool Portal = portal;
        public readonly bool Behind = behind;
        public readonly float DistanceSqr = distanceSqr;
    }

    /// Vanilla type order first, so no piece appears before the terrain edits and supports under
    /// it. Within a type: portals, so a preview finds its far portal early, then objects in front
    /// of the far portal, nearest first.
    private static readonly Comparison<Candidate> ByPriority = (a, b) =>
    {
        int order = ((int)b.Zdo.Type).CompareTo((int)a.Zdo.Type);
        if (order != 0)
            return order;
        if (a.Portal != b.Portal)
            return a.Portal ? -1 : 1;
        if (a.Behind != b.Behind)
            return a.Behind ? 1 : -1;
        return a.DistanceSqr.CompareTo(b.DistanceSqr);
    };

    /// Creates the missing objects within PrimeRadius of a destination, the same set
    /// GetNearState waits for, in ByPriority order.
    private static void CreateNearDestination(ZNetScene scene, ZoneSystem zoneSystem, Destination destination, ref int budget)
    {
        // A fast teleport checks around the arrival point, about 1.5 m from the far portal, so the
        // pass reaches a little further than the check.
        float radius = PrimeRadius + 2f;
        float radiusSqr = radius * radius;
        Candidates.Clear();
        CandidateSectors.Clear();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                Vector2s zone = new(destination.Zone.x + dx, destination.Zone.y + dy);
                if (zoneSystem.m_zones.ContainsKey(zone))
                    ZDOMan.instance.FindObjects(zone, Candidates, CandidateSectors);
            }
        }

        Queue.Clear();
        foreach (ZDO zdo in Candidates)
        {
            if (!zdo.IsValid() || scene.m_instances.ContainsKey(zdo) || !scene.IsPrefabZDOValid(zdo))
                continue;

            Vector3 offset = zdo.GetPosition() - destination.Position;
            float distanceSqr = offset.sqrMagnitude;
            // A terrain edit shapes its whole zone, so its distance does not matter.
            if (zdo.Type != ZDO.ObjectType.Terrain && distanceSqr > radiusSqr)
                continue;

            offset.y = 0f;
            Queue.Add(new Candidate(zdo, Portals.IsPortal(zdo), Vector3.Dot(offset, destination.Forward) < -BehindAllowance, distanceSqr));
        }

        Candidates.Clear();
        if (Queue.Count == 0)
            return;

        Queue.Sort(ByPriority);
        UnfinishedZones.Clear();
        foreach (Candidate candidate in Queue)
        {
            if (budget == 0)
                break;

            ZDO zdo = candidate.Zdo;
            Vector2s zone = zdo.GetSector();
            // Lower types in a zone wait while a higher type there is unfinished.
            if (UnfinishedZones.TryGetValue(zone, out ZDO.ObjectType unfinished) && zdo.Type < unfinished)
                continue;
            if (!zoneSystem.IsZoneReadyForType(zone, zdo.Type))
            {
                UnfinishedZones[zone] = zdo.Type;
                continue;
            }

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

        Queue.Clear();
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
        return GetNearState(origin, forward, behindAllowance, out _) == NearState.Built;
    }

    internal enum NearState
    {
        ZonesMissing,
        ObjectsMissing,
        Built
    }

    /// blocker is the first missing object found, when the state is ObjectsMissing.
    internal static NearState GetNearState(Vector3 origin, Vector3 forward, float behindAllowance, out ZDO? blocker)
    {
        blocker = null;
        ZoneSystem zoneSystem = ZoneSystem.instance;
        ZNetScene scene = ZNetScene.instance;
        if (zoneSystem == null || scene == null || ZDOMan.instance == null)
            return NearState.ZonesMissing;

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
                {
                    NearObjects.Clear();
                    return NearState.ZonesMissing;
                }
                ZDOMan.instance.FindObjects(zone, NearObjects, NearSectors);
            }
        }

        foreach (ZDO zdo in NearObjects)
        {
            if (!zdo.IsValid() || !scene.IsPrefabZDOValid(zdo) || scene.HaveInstance(zdo))
                continue;

            // A terrain edit shapes its whole zone, so its position does not matter. The distance
            // includes height, as in CreateNearDestination: a dungeon interior sits thousands of
            // meters above its entrance, and the pass never creates it.
            Vector3 offset = zdo.GetPosition() - origin;
            if (zdo.Type != ZDO.ObjectType.Terrain && offset.sqrMagnitude > radiusSqr)
                continue;
            offset.y = 0f;
            if (zdo.Type != ZDO.ObjectType.Terrain && Vector3.Dot(offset, forward) < -behindAllowance)
                continue;

            blocker = zdo;
            break;
        }

        NearObjects.Clear();
        return blocker == null ? NearState.Built : NearState.ObjectsMissing;
    }

    /// For the perf log: what an object is and where it sits relative to origin.
    internal static string Describe(ZDO zdo, Vector3 origin)
    {
        GameObject? prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
        Vector3 offset = zdo.GetPosition() - origin;
        float height = offset.y;
        offset.y = 0f;
        bool zoneReady = ZoneSystem.instance == null || ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type);
        return $"{(prefab != null ? prefab.name : zdo.GetPrefab().ToString())} ({zdo.Type}, {offset.magnitude:F0} m across, " +
               $"{height:F0} m up{(zoneReady ? "" : ", its zone is still loading a location")})";
    }
}
