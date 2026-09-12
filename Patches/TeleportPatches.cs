using HarmonyLib;
using UnityEngine;

namespace OttoBifrost.Patches;

/// Ends a teleport to a preloaded destination as soon as the arrival area is ready, instead of
/// after the fixed vanilla wait.
///
/// IsAreaReady alone is not enough on a dedicated server: it passes before the server sends
/// the area, and floors then appear after you land. So the teleport also waits until the
/// server reports the destination complete, or the arrival object count stops changing.
[HarmonyPatch]
internal static class TeleportPatches
{
    // Vanilla waits until the timer passes 2 s before it moves the player. Starting there skips
    // that wait, and the thresholds below keep their vanilla meaning.
    private const float SkipVanillaWait = 2.1f;
    private const float MinimumTeleportTime = 0.5f;
    private const float FloorSearchTimeout = 3f;
    // Vanilla holds a distant teleport until its timer passes 8. A hand-over at that mark leaves
    // vanilla in the state it expects, so a fast teleport is never slower than vanilla.
    private const float FallbackToVanillaTime = 8f;
    private const float ServerReactionTime = 1f;
    private const float ArrivalStableTime = 0.5f;
    private const float PreloadedTolerance = 10f;
    private const float ArrivalHoldSeconds = 5f;

    private static bool _awaitingArrival;
    private static bool _fastTrip;
    private static bool _serverComplete;
    private static float _startedAt;
    private static float _movedAt = -1f;
    private static int _arrivalObjects = -1;
    private static int _arrivalObjectsAtMove;
    private static float _arrivalChangedAt;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
    private static void PlayerTeleportToPostfix(Player __instance, Vector3 pos, bool distantTeleport, bool __result)
    {
        if (!__result || __instance != Player.m_localPlayer)
            return;

        _awaitingArrival = true;
        Destinations.BeginTeleport();

        // Dungeon doors are not distant teleports. Vanilla can send the player back from those
        // with "portal blocked", which the fast path would skip.
        _fastTrip = distantTeleport && OttoBifrostPlugin.FastTeleport.Value && Destinations.IsNear(pos, PreloadedTolerance);
        // Read before the player moves, because the source portal stops tracking its far end then.
        _serverComplete = DestinationSync.IsDestinationComplete(pos);
        _startedAt = Time.time;
        _movedAt = -1f;
        _arrivalObjects = -1;

        if (PerfStats.Enabled)
            OttoBifrostPlugin.Log.LogInfo($"Teleport start: fast path {_fastTrip}, server reports destination complete {_serverComplete}");

        if (_fastTrip)
            __instance.m_teleportTimer = SkipVanillaWait;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateTeleport))]
    private static bool PlayerUpdateTeleportPrefix(Player __instance, float dt)
    {
        if (!_fastTrip || __instance != Player.m_localPlayer || !__instance.m_teleporting)
            return true;

        if (ZoneSystem.instance == null || ZNetScene.instance == null || !OttoBifrostPlugin.FastTeleport.Value)
        {
            _fastTrip = false;
            return true;
        }

        __instance.m_teleportCooldown = 0f;
        __instance.m_teleportTimer += dt;
        if (__instance.m_teleportTimer <= MinimumTeleportTime)
            return false;

        Vector3 target = __instance.m_teleportTargetPos;
        HoldAtTarget(__instance);

        if (_movedAt < 0f)
        {
            _movedAt = Time.time;
            MovePlayerZdo(__instance, target);
            // ZNet otherwise sends the new position within 2 s, and until then the server keeps
            // sending the area the player left.
            if (ZNet.instance != null)
            {
                ZNet.instance.SetReferencePosition(target);
                DestinationSync.ForceReferencePositionSend();
            }
        }

        TrackArrivalObjects(target);
        bool dataSettled = _serverComplete ||
                           (Time.time - _movedAt >= ServerReactionTime && Time.time - _arrivalChangedAt >= ArrivalStableTime);
        bool areaReady = ZNetScene.instance.IsAreaReady(target);
        bool floorFound = ZoneSystem.instance.FindFloor(target, out float floorHeight);

        if (areaReady && dataSettled && (floorFound || __instance.m_teleportTimer > FloorSearchTimeout))
        {
            if (floorFound)
                __instance.transform.position = new Vector3(target.x, floorHeight, target.z);
            __instance.m_teleportTimer = 0f;
            __instance.m_teleporting = false;
            __instance.ResetCloth();
            _fastTrip = false;
            LogOutcome("finished", areaReady, dataSettled, floorFound);
            return false;
        }

        if (__instance.m_teleportTimer > FallbackToVanillaTime)
        {
            _fastTrip = false;
            LogOutcome("handed over to vanilla", areaReady, dataSettled, floorFound);
            return true;
        }

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdateTeleport))]
    private static void PlayerUpdateTeleportPostfix(Player __instance)
    {
        if (!_awaitingArrival || __instance != Player.m_localPlayer || __instance.m_teleporting)
            return;

        _awaitingArrival = false;
        Destinations.EndTeleport(__instance.transform.position, ArrivalHoldSeconds);
    }

    private static void HoldAtTarget(Player player)
    {
        player.transform.SetPositionAndRotation(player.m_teleportTargetPos, player.m_teleportTargetRot);
        player.m_body.linearVelocity = Vector3.zero;
        player.m_maxAirAltitude = player.m_teleportTargetPos.y;
        if (EnvMan.instance != null)
            EnvMan.instance.ForceInstantEnvironmentSwitch();
        player.SetLookDir(player.m_teleportTargetRot * Vector3.forward);
    }

    // ZNetScene destroys every instance whose ZDO is outside the area around the reference
    // position, and the player's ZDO normally follows the rigidbody only in LateUpdate. Without
    // this, a create-destroy pass between the move and LateUpdate destroys the local player.
    private static void MovePlayerZdo(Player player, Vector3 target)
    {
        player.m_body.position = target;
        ZSyncTransform sync = player.GetComponent<ZSyncTransform>();
        if (sync != null)
            sync.SyncNow();
    }

    private static void TrackArrivalObjects(Vector3 target)
    {
        int count = DestinationSync.CountKnownObjects(target);
        if (count == _arrivalObjects)
            return;

        if (_arrivalObjects < 0)
            _arrivalObjectsAtMove = count;
        _arrivalObjects = count;
        _arrivalChangedAt = Time.time;
    }

    private static void LogOutcome(string outcome, bool areaReady, bool dataSettled, bool floorFound)
    {
        if (!PerfStats.Enabled)
            return;
        OttoBifrostPlugin.Log.LogInfo(
            $"Teleport {outcome} after {Time.time - _startedAt:F2} s: server reports complete {_serverComplete}, " +
            $"area ready {areaReady}, data settled {dataSettled}, floor {floorFound}, " +
            $"arrival objects {_arrivalObjects} ({_arrivalObjects - _arrivalObjectsAtMove} received since the move)");
    }

    internal static void Reset()
    {
        _awaitingArrival = false;
        _fastTrip = false;
        _serverComplete = false;
        _movedAt = -1f;
        _arrivalObjects = -1;
    }
}
