using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OttoBifrost.Patches;

/// A dedicated server sends a client only the objects near that client. Without this sync,
/// the client can preload only bases it already visited in the session.
///
/// ZNet sends the client's position to the server only every 2 s, so the client forces an
/// early send, and the server checks distance on every send, not when a list arrives.
[HarmonyPatch]
internal static class DestinationSync
{
    internal const string SetDestinationsRpc = $"{OttoBifrostPlugin.ModName}SetDestinations";
    internal const string DestinationStateRpc = $"{OttoBifrostPlugin.ModName}DestinationState";

    private const int MaxDestinationsPerPeer = 8;
    // 3x3 zones hold the remote portal, the arrival point that IsAreaReady checks, and what
    // the preview shows.
    private const int ServerZoneRadius = 1;
    // The trigger radius is 15 m. The margin covers the proximity root offset and player
    // movement between report and check.
    private const float MaxSourcePortalDistance = 30f;
    private const float MinSendInterval = 0.25f;
    // Hysteresis, so a burning fire or a moving animal does not flip a complete destination.
    private const int IncompleteThreshold = 16;
    // The teleport target is the remote portal position plus 1 m forward and 1 m up.
    private const float ExitPointTolerance = 5f;

    #region Client

    private static readonly HashSet<ZDOID> TrackedRemotePortals = new();
    private static readonly Dictionary<ZDOID, bool> DestinationComplete = new();
    private static bool _dirty;
    private static float _lastSendTime = float.MinValue;

    internal static void Track(ZDOID remotePortal)
    {
        if (remotePortal != ZDOID.None && TrackedRemotePortals.Add(remotePortal))
            _dirty = true;
    }

    internal static void Untrack(ZDOID remotePortal)
    {
        if (remotePortal != ZDOID.None && TrackedRemotePortals.Remove(remotePortal))
        {
            DestinationComplete.Remove(remotePortal);
            _dirty = true;
        }
    }

    internal static void ClientUpdate()
    {
        if (!_dirty || ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
            return;
        if (Time.time - _lastSendTime < MinSendInterval)
            return;

        long serverPeer = ZRoutedRpc.instance.GetServerPeerID();
        // 0 means no server connection yet.
        if (serverPeer == 0)
            return;

        _dirty = false;
        _lastSendTime = Time.time;

        // A new list resets every state on the server, so the old states are stale.
        DestinationComplete.Clear();

        ZPackage pkg = new();
        int count = Mathf.Min(TrackedRemotePortals.Count, MaxDestinationsPerPeer);
        pkg.Write(count);
        int written = 0;
        foreach (ZDOID id in TrackedRemotePortals)
        {
            if (written++ >= count)
                break;
            pkg.Write(id);
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(serverPeer, SetDestinationsRpc, pkg);
        ForceReferencePositionSend();
    }

    /// Until the server has the new position, it treats the player as standing where they were.
    internal static void ForceReferencePositionSend()
    {
        if (ZNet.instance != null && !ZNet.instance.IsServer())
            ZNet.instance.m_periodicSendTimer = 2f;
    }

    /// Always true on the server. False on a client whose server does not run the mod.
    internal static bool IsDestinationComplete(ZDOID remotePortal)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer())
            return true;
        return DestinationComplete.TryGetValue(remotePortal, out bool complete) && complete;
    }

    /// Always true on the server, because a server already holds every object.
    internal static bool IsDestinationComplete(Vector3 teleportTarget)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer())
            return true;
        if (ZDOMan.instance == null)
            return false;

        float toleranceSqr = ExitPointTolerance * ExitPointTolerance;
        foreach (KeyValuePair<ZDOID, bool> kvp in DestinationComplete)
        {
            if (!kvp.Value)
                continue;
            ZDO remote = ZDOMan.instance.GetZDO(kvp.Key);
            if (remote != null && (remote.GetPosition() - teleportTarget).sqrMagnitude <= toleranceSqr)
                return true;
        }

        return false;
    }

    internal static void OnDestinationState(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null)
            return;
        if (sender != ZRoutedRpc.instance.GetServerPeerID())
            return;

        ZDOID id = pkg.ReadZDOID();
        bool complete = pkg.ReadBool();
        if (TrackedRemotePortals.Contains(id))
            DestinationComplete[id] = complete;

        if (PerfStats.Enabled)
            OttoBifrostPlugin.Log.LogInfo($"Destination {id}: server reports {(complete ? "complete" : "still sending")}");
    }

    /// Counts the same 3x3 zones that vanilla IsAreaReady checks.
    internal static int CountKnownObjects(Vector3 point)
    {
        ZDOMan zdoMan = ZDOMan.instance;
        if (zdoMan == null)
            return 0;

        Vector2s zone = ZoneSystem.GetZone(point);
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                ZoneSystem.SectorIndex index = ZoneSystem.SectorToIndex(zone.x + dx, zone.y + dy);
                if (index.Sector < zdoMan.m_objectsBySector.Length)
                    count += zdoMan.m_objectsBySector[index.Sector]?.Count ?? 0;
                if (zdoMan.m_portalObjects.TryGetValue(index, out List<ZDO> portals))
                    count += portals.Count;
            }
        }

        return count;
    }

    #endregion

    #region Server

    private sealed class PeerDestination(ZDOID remoteId, Vector2s remoteZone, Vector3 sourcePortalPos)
    {
        public readonly ZDOID RemoteId = remoteId;
        public readonly Vector2s RemoteZone = remoteZone;
        public readonly Vector3 SourcePortalPos = sourcePortalPos;
        public bool? ReportedComplete;
    }

    private static readonly Dictionary<long, List<PeerDestination>> PeerDestinations = new();
    private static readonly List<ZDO> Candidates = new();

    internal static void OnSetDestinations(long sender, ZPackage pkg)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
            return;

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null)
            return;

        List<PeerDestination> accepted = new();
        int requested;
        try
        {
            requested = Mathf.Min(pkg.ReadInt(), MaxDestinationsPerPeer);
            for (int i = 0; i < requested; i++)
            {
                ZDOID remoteId = pkg.ReadZDOID();
                ZDO remote = ZDOMan.instance.GetZDO(remoteId);
                if (remote == null || !Portals.IsPortal(remote))
                    continue;

                // ZDOMan.ConnectPortals stores the connection on both portals, so the remote
                // portal leads back to the one the player stands near.
                ZDO source = ZDOMan.instance.GetZDO(remote.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal));
                if (source == null)
                    continue;

                // No distance check here: the player's position can be 2 s old, and right
                // after a teleport a check here rejected every portal.
                accepted.Add(new PeerDestination(remoteId, ZoneSystem.GetZone(remote.GetPosition()), source.GetPosition()));
            }
        }
        catch (Exception ex)
        {
            OttoBifrostPlugin.Log.LogWarning($"Bad destination list from peer {sender}: {ex.Message}");
            return;
        }

        if (accepted.Count == 0)
            PeerDestinations.Remove(sender);
        else
            PeerDestinations[sender] = accepted;

        if (PerfStats.Enabled)
            OttoBifrostPlugin.Log.LogInfo($"Destinations from peer {sender}: {accepted.Count} of {requested} accepted");
    }

    /// Appends after vanilla's sorted list, so the peer's own area goes first within the send budget.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.CreateSyncList))]
    private static void ZDOManCreateSyncListPostfix(ZDOMan __instance, ZDOMan.ZDOPeer peer, List<ZDO> toSync)
    {
        if (PeerDestinations.Count == 0 || ZNet.instance == null || !ZNet.instance.IsServer())
            return;
        if (!PeerDestinations.TryGetValue(peer.m_peer.m_uid, out List<PeerDestination> destinations))
            return;

        long start = PerfStats.Begin();
        Vector3 refPos = peer.m_peer.GetRefPos();
        float maxDistanceSqr = MaxSourcePortalDistance * MaxSourcePortalDistance;

        // Sectors vanilla already added are skipped, because SendZDOs writes duplicates twice.
        // A destination sector in the peer's distant ring sends only its distant objects until
        // the peer comes closer.
        HashSet<ZoneSystem.SectorIndex> visited = __instance.m_visitedSectorIndices;
        int appendedTotal = 0;
        foreach (PeerDestination dest in destinations)
        {
            // Also true while the server still has the player's old position. A later send retries.
            if ((dest.SourcePortalPos - refPos).sqrMagnitude > maxDistanceSqr)
                continue;

            Candidates.Clear();
            for (int dy = -ServerZoneRadius; dy <= ServerZoneRadius; dy++)
            {
                for (int dx = -ServerZoneRadius; dx <= ServerZoneRadius; dx++)
                    __instance.FindObjects(new Vector2s(dest.RemoteZone.x + dx, dest.RemoteZone.y + dy), Candidates, visited);
            }

            int waiting = 0;
            foreach (ZDO zdo in Candidates)
            {
                if (peer.ShouldSend(zdo))
                {
                    toSync.Add(zdo);
                    waiting++;
                }
            }

            appendedTotal += waiting;
            ReportState(peer.m_peer.m_uid, dest, waiting);
        }

        Candidates.Clear();
        if (PerfStats.Enabled)
        {
            PerfStats.ServerObjectsAppended += appendedTotal;
            PerfStats.ServerSyncTicks += PerfStats.Elapsed(start);
        }
    }

    /// Objects appended but not written in this send stay waiting, so "complete" is reported
    /// only after the last one went out.
    private static void ReportState(long peerUid, PeerDestination dest, int waiting)
    {
        bool? state = waiting == 0 ? true
            : waiting >= IncompleteThreshold ? false
            : dest.ReportedComplete;
        if (state == null || state == dest.ReportedComplete || ZRoutedRpc.instance == null)
            return;

        dest.ReportedComplete = state;
        ZPackage pkg = new();
        pkg.Write(dest.RemoteId);
        pkg.Write(state.Value);
        ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, DestinationStateRpc, pkg);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemovePeer))]
    private static void ZDOManRemovePeerPostfix(ZNetPeer netPeer)
    {
        if (netPeer != null)
            PeerDestinations.Remove(netPeer.m_uid);
    }

    #endregion

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    private static void GameStartPostfix()
    {
        ZRoutedRpc.instance.Register<ZPackage>(SetDestinationsRpc, OnSetDestinations);
        ZRoutedRpc.instance.Register<ZPackage>(DestinationStateRpc, OnDestinationState);
    }

    internal static void Clear()
    {
        TrackedRemotePortals.Clear();
        DestinationComplete.Clear();
        _dirty = false;
        _lastSendTime = float.MinValue;
        PeerDestinations.Clear();
        Candidates.Clear();
    }
}
