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

    private static readonly List<ZDO> Candidates = new();
    private static readonly HashSet<ZoneSystem.SectorIndex> CandidateSectors = new();
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

    /// Missing zones are taken ring by ring across all destinations, so each portal gets its
    /// centre zone before any gets an outer ring.
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

        bool haveMissing = false;
        Vector2s missing = default;
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
                        else if (!haveMissing)
                        {
                            haveMissing = true;
                            missing = zone;
                        }
                    }
                }
            }
        }

        // A false result from PokeLocalZone means the heightmap is still building. Poking more
        // zones would only queue more builds.
        if (!__result && haveMissing && __instance.PokeLocalZone(missing))
        {
            __result = true;
            if (PerfStats.Enabled)
                PerfStats.ZonesSpawned++;
        }

        PerfStats.ZonePatchTicks += PerfStats.Elapsed(start);
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

    private static readonly bool[] PortalPasses = { true, false };
}
