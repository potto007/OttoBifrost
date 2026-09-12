using OttoBifrost.Patches;
using UnityEngine;

namespace OttoBifrost.Components;

/// Keeps the far end of this portal loading while the local player stands near it.
public sealed class DestinationLoader : MonoBehaviour
{
    private const float ReachDistance = 15f;
    private const float CheckInterval = 0.2f;
    // The loaded area grows one ring at a time, so walking up does not cause a hitch.
    private const float SecondsPerRing = 0.4f;

    private TeleportWorld _portal = null!;
    private ZDOID _portalId = ZDOID.None;
    private ZDOID _farEndId = ZDOID.None;
    private float _requestedAt;
    private float _nextCheck;

    private void Awake()
    {
        _portal = GetComponent<TeleportWorld>();
        // Spreads the checks of many portals across frames.
        _nextCheck = Time.time + Random.value * CheckInterval;
    }

    private void Update()
    {
        if (Time.time < _nextCheck)
            return;
        _nextCheck = Time.time + CheckInterval;

        Player player = Player.m_localPlayer;
        ZDO? portal = Portals.ZdoOf(_portal);
        ZDO? farEnd = portal != null ? Portals.FarEnd(portal) : null;
        if (player == null || portal == null || farEnd == null || !OttoBifrostPlugin.PreloadDestinations.Value)
        {
            Release();
            return;
        }

        Vector3 anchor = _portal.m_proximityRoot != null ? _portal.m_proximityRoot.position : transform.position;
        float distance = Vector3.Distance(player.transform.position, anchor);
        if (distance > ReachDistance)
        {
            Release();
            return;
        }

        // Also catches a tag change that connects the portal somewhere else.
        if (farEnd.m_uid != _farEndId)
        {
            Release();
            _portalId = portal.m_uid;
            _farEndId = farEnd.m_uid;
            _requestedAt = Time.time;
            DestinationSync.Track(_farEndId);
        }

        int grown = 1 + (int)((Time.time - _requestedAt) / SecondsPerRing);
        Destinations.Request(_portalId, farEnd.GetPosition(), Mathf.Min(RadiusFor(distance), grown), distance);
    }

    private void OnDisable()
    {
        Release();
    }

    private void Release()
    {
        if (_farEndId == ZDOID.None)
            return;

        Destinations.Release(_portalId);
        DestinationSync.Untrack(_farEndId);
        _portalId = ZDOID.None;
        _farEndId = ZDOID.None;
    }

    private static int RadiusFor(float distance)
    {
        if (distance <= 5f)
            return 3;
        return distance <= 10f ? 2 : 1;
    }
}
