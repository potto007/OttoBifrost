using System;
using System.Collections.Generic;
using OttoBifrost.Patches;
using UnityEngine;

namespace OttoBifrost;

internal readonly struct Destination(Vector3 position, int radius)
{
    public readonly Vector3 Position = position;
    public readonly Vector2s Zone = ZoneSystem.GetZone(position);
    public readonly int Radius = radius;
}

/// The areas the zone and object patches keep loaded, in load order.
internal static class Destinations
{
    private sealed class PortalRequest
    {
        public Vector3 Position;
        public int Radius;
        public float Distance;
    }

    private const int ArrivalRadius = 3;
    // Radius 1 holds the far portal and its surroundings, which is all a preview needs.
    private const int SecondaryRadius = 1;
    // Portals in a hub room stand a few meters apart. Without a margin, the nearest flips as
    // the player walks, and the full-size area moves between bases each time.
    private const float NearestSwitchMargin = 2f;

    private static readonly Dictionary<ZDOID, PortalRequest> Requests = new();
    private static readonly List<PortalRequest> Others = new();
    private static readonly List<Destination> Pending = new();
    private static readonly Comparison<PortalRequest> ByDistance = (a, b) => a.Distance.CompareTo(b.Distance);
    private static readonly HashSet<ZDOID> DeferredReleases = new();
    private static ZDOID _nearest = ZDOID.None;
    private static bool _teleporting;
    private static bool _holdingArrival;
    private static Vector3 _arrival;
    private static float _arrivalReleaseTime = float.MaxValue;

    // Every patch walks this in order, so the order is the load priority.
    internal static Destination[] Active = Array.Empty<Destination>();

    internal static bool Any => Active.Length != 0;

    /// distance is from the player to the portal, and the nearest portal loads first.
    internal static void Request(ZDOID portal, Vector3 farEnd, int radius, float distance)
    {
        if (!Requests.TryGetValue(portal, out PortalRequest request))
        {
            request = new PortalRequest();
            Requests.Add(portal, request);
        }

        DeferredReleases.Remove(portal);
        request.Position = farEnd;
        request.Radius = radius;
        request.Distance = distance;
        Rebuild();
    }

    internal static void Release(ZDOID portal)
    {
        // The source portal unloads behind the player during a teleport. Its request holds the
        // arrival area, so it must last until the player has landed.
        if (_teleporting)
        {
            if (Requests.ContainsKey(portal))
                DeferredReleases.Add(portal);
            return;
        }

        if (Requests.Remove(portal))
            Rebuild();
    }

    internal static void BeginTeleport()
    {
        _teleporting = true;
    }

    /// The landing spot stays loaded for holdSeconds, after the nearest portal's destination.
    internal static void EndTeleport(Vector3 landing, float holdSeconds)
    {
        _teleporting = false;
        foreach (ZDOID portal in DeferredReleases)
            Requests.Remove(portal);
        DeferredReleases.Clear();

        _holdingArrival = true;
        _arrival = landing;
        _arrivalReleaseTime = Time.time + holdSeconds;
        Rebuild();
    }

    internal static void Tick()
    {
        if (_holdingArrival && Time.time >= _arrivalReleaseTime)
        {
            _holdingArrival = false;
            _arrivalReleaseTime = float.MaxValue;
            Rebuild();
        }
    }

    internal static bool Covers(Vector2s zone)
    {
        foreach (Destination destination in Active)
        {
            if (Mathf.Abs(zone.x - destination.Zone.x) <= destination.Radius &&
                Mathf.Abs(zone.y - destination.Zone.y) <= destination.Radius)
                return true;
        }

        return false;
    }

    internal static bool IsNear(Vector3 position, float tolerance)
    {
        float toleranceSqr = tolerance * tolerance;
        foreach (Destination destination in Active)
        {
            if ((destination.Position - position).sqrMagnitude < toleranceSqr)
                return true;
        }

        return false;
    }

    internal static void Clear()
    {
        Requests.Clear();
        DeferredReleases.Clear();
        _teleporting = false;
        _nearest = ZDOID.None;
        _holdingArrival = false;
        _arrivalReleaseTime = float.MaxValue;
        Active = Array.Empty<Destination>();
    }

    private static void Rebuild()
    {
        ZDOID nearest = PickNearest();
        if (PerfStats.Enabled && _nearest != ZDOID.None && nearest != ZDOID.None && nearest != _nearest)
            PerfStats.NearestSwitches++;
        _nearest = nearest;

        Pending.Clear();
        Others.Clear();
        foreach (KeyValuePair<ZDOID, PortalRequest> entry in Requests)
        {
            if (entry.Key == nearest)
                Pending.Add(new Destination(entry.Value.Position, entry.Value.Radius));
            else
                Others.Add(entry.Value);
        }

        // The nearest portal goes first even just after landing, so a portal taken a few seconds
        // later is ready. The player's own active area already covers the landing spot, so next
        // to a portal it keeps only the zones a preview would, and its outer objects do not queue
        // ahead of the destination's.
        if (_holdingArrival)
            Pending.Add(new Destination(_arrival, nearest != ZDOID.None ? SecondaryRadius : ArrivalRadius));

        Others.Sort(ByDistance);
        foreach (PortalRequest other in Others)
            Pending.Add(new Destination(other.Position, Mathf.Min(other.Radius, SecondaryRadius)));

        if (!MatchesActive(Pending))
            Active = Pending.ToArray();
    }

    private static ZDOID PickNearest()
    {
        ZDOID best = ZDOID.None;
        float bestDistance = float.MaxValue;
        foreach (KeyValuePair<ZDOID, PortalRequest> entry in Requests)
        {
            if (entry.Value.Distance < bestDistance)
            {
                best = entry.Key;
                bestDistance = entry.Value.Distance;
            }
        }

        if (best != _nearest && Requests.TryGetValue(_nearest, out PortalRequest current) &&
            bestDistance > current.Distance - NearestSwitchMargin)
            return _nearest;

        return best;
    }

    private static bool MatchesActive(List<Destination> candidate)
    {
        if (candidate.Count != Active.Length)
            return false;

        for (int i = 0; i < Active.Length; i++)
        {
            if (Active[i].Position != candidate[i].Position || Active[i].Radius != candidate[i].Radius)
                return false;
        }

        return true;
    }
}
