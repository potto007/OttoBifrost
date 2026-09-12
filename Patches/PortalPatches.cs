using HarmonyLib;
using OttoBifrost.Components;

namespace OttoBifrost.Patches;

[HarmonyPatch]
internal static class PortalPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
    private static void TeleportWorldAwakePostfix(TeleportWorld __instance)
    {
        // A dedicated server has no player or camera. It only answers DestinationSync requests.
        if (Player.IsPlacementGhost(__instance.gameObject) || (ZNet.instance != null && ZNet.instance.IsDedicated()))
            return;

        __instance.gameObject.AddComponent<DestinationLoader>();
        __instance.gameObject.AddComponent<PortalWindow>();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnDestroy))]
    private static void ZNetSceneOnDestroyPostfix()
    {
        Destinations.Clear();
        TeleportPatches.Reset();
        DestinationSync.Clear();
    }
}
