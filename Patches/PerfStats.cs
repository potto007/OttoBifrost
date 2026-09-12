using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace OttoBifrost.Patches;

/// Times are main-thread CPU time. Camera.Render only submits GPU work, so the preview GPU
/// cost is not included. Frame times include everything the game does, not only this mod.
[HarmonyPatch]
internal static class PerfStats
{
    private const float ReportInterval = 5f;

    // Refreshed once per frame from the plugin Update, so hot paths read a plain field.
    internal static bool Enabled;

    private static float _windowTime;
    private static int _frames;
    private static float _worstFrame;

    internal static int PreviewRenders;
    internal static long PreviewRenderTicks;
    internal static long PreviewRenderMaxTicks;
    internal static int ZonesSpawned;
    internal static int ZoneWaits;
    internal static float ZoneWaitTotal;
    internal static float ZoneWaitMax;
    internal static int TerrainQueueMax;
    // Written on vanilla's heightmap thread, so only through Interlocked.
    private static int _terrainBuilds;
    private static long _terrainBuildTicks;
    internal static long ZonePatchTicks;
    internal static long SectorPatchTicks;
    internal static int ForcedCreates;
    internal static long CreatePatchTicks;
    internal static int NearestSwitches;
    internal static int HeightmapForceCalls;
    internal static int HeightmapsForced;
    internal static long HeightmapForceTicks;
    internal static long HeightmapForceMaxTicks;
    internal static int ServerObjectsAppended;
    internal static long ServerSyncTicks;

    internal static long Begin()
    {
        return Enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    internal static long Elapsed(long start)
    {
        return start == 0L ? 0L : Stopwatch.GetTimestamp() - start;
    }

    private static string Ms(long ticks)
    {
        return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1");
    }

    internal static void Tick(float unscaledDeltaTime, bool enabled)
    {
        if (enabled != Enabled)
        {
            Enabled = enabled;
            Reset();
        }

        if (!enabled)
            return;

        _frames++;
        _windowTime += unscaledDeltaTime;
        if (unscaledDeltaTime > _worstFrame)
            _worstFrame = unscaledDeltaTime;

        if (_windowTime < ReportInterval)
            return;

        float avgFrameMs = _windowTime * 1000f / _frames;
        string previewAvg = PreviewRenders > 0 ? Ms(PreviewRenderTicks / PreviewRenders) : "0.0";
        float zoneWaitAvg = ZoneWaits > 0 ? ZoneWaitTotal / ZoneWaits : 0f;
        long terrainTicks = Interlocked.Read(ref _terrainBuildTicks);
        double terrainBusy = terrainTicks * 100.0 / Stopwatch.Frequency / _windowTime;
        OttoBifrostPlugin.Log.LogInfo(
            $"Perf {_windowTime:F1}s: frames {_frames} avg {avgFrameMs:F1} ms worst {_worstFrame * 1000f:F1} ms" +
            $" | destinations {Destinations.Active.Length}, nearest switches {NearestSwitches}" +
            $" | preview renders {PreviewRenders} avg {previewAvg} ms max {Ms(PreviewRenderMaxTicks)} ms" +
            $" | zones spawned {ZonesSpawned} (zone patch {Ms(ZonePatchTicks)} ms)" +
            $", wait at front of line avg {zoneWaitAvg:F2} s max {ZoneWaitMax:F2} s over {ZoneWaits}" +
            $" | terrain thread {Interlocked.Exchange(ref _terrainBuilds, 0)} builds, {Ms(terrainTicks)} ms, busy {terrainBusy:F0}%, queue max {TerrainQueueMax}" +
            $" | sector patch {Ms(SectorPatchTicks)} ms" +
            $" | forced creates {ForcedCreates} ({Ms(CreatePatchTicks)} ms)" +
            $" | heightmap force {HeightmapForceCalls} calls, {HeightmapsForced} rebuilt, {Ms(HeightmapForceTicks)} ms total, max {Ms(HeightmapForceMaxTicks)} ms" +
            $" | server appended {ServerObjectsAppended} objects ({Ms(ServerSyncTicks)} ms)" +
            $" | client objects {ZDOMan.instance?.m_objectsByID.Count ?? 0}, received {ZDOMan.instance?.m_zdosRecvLastSec ?? 0}/s");
        Reset();
    }

    internal static void AddPreviewRender(long start)
    {
        long elapsed = Elapsed(start);
        if (elapsed == 0L)
            return;
        PreviewRenders++;
        PreviewRenderTicks += elapsed;
        if (elapsed > PreviewRenderMaxTicks)
            PreviewRenderMaxTicks = elapsed;
    }

    /// seconds runs from a destination zone reaching the front of the need order to its spawn.
    /// With its terrain built ahead, that is the ticks spent waiting for vanilla's own zones.
    internal static void AddZoneWait(float seconds)
    {
        ZoneWaits++;
        ZoneWaitTotal += seconds;
        if (seconds > ZoneWaitMax)
            ZoneWaitMax = seconds;
    }

    private static void Reset()
    {
        _windowTime = 0f;
        _frames = 0;
        _worstFrame = 0f;
        PreviewRenders = 0;
        PreviewRenderTicks = 0L;
        PreviewRenderMaxTicks = 0L;
        ZonesSpawned = 0;
        ZoneWaits = 0;
        ZoneWaitTotal = 0f;
        ZoneWaitMax = 0f;
        TerrainQueueMax = 0;
        Interlocked.Exchange(ref _terrainBuilds, 0);
        Interlocked.Exchange(ref _terrainBuildTicks, 0L);
        ZonePatchTicks = 0L;
        SectorPatchTicks = 0L;
        ForcedCreates = 0;
        CreatePatchTicks = 0L;
        NearestSwitches = 0;
        HeightmapForceCalls = 0;
        HeightmapsForced = 0;
        HeightmapForceTicks = 0L;
        HeightmapForceMaxTicks = 0L;
        ServerObjectsAppended = 0;
        ServerSyncTicks = 0L;
    }

    /// Runs on vanilla's heightmap thread, which builds the terrain data for every zone one at a
    /// time. Its busy share shows whether more terrain threads could load zones faster.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.Build))]
    private static void HeightmapBuilderBuildPrefix(out long __state)
    {
        __state = Begin();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.Build))]
    private static void HeightmapBuilderBuildPostfix(long __state)
    {
        long elapsed = Elapsed(__state);
        if (elapsed == 0L)
            return;
        Interlocked.Increment(ref _terrainBuilds);
        Interlocked.Add(ref _terrainBuildTicks, elapsed);
    }

    /// Ship, Vagon and SnapToGround call ForceGenerateAll from Awake, which rebuilds every
    /// queued heightmap at once. Each preloaded cart or ship can cause a hitch.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll))]
    private static void HeightmapForceGenerateAllPrefix(out long __state)
    {
        __state = Begin();
        if (__state == 0L)
            return;

        HeightmapForceCalls++;
        foreach (Heightmap heightmap in Heightmap.s_heightmaps)
        {
            if (heightmap.HaveQueuedRebuild())
                HeightmapsForced++;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.ForceGenerateAll))]
    private static void HeightmapForceGenerateAllPostfix(long __state)
    {
        long elapsed = Elapsed(__state);
        if (elapsed == 0L)
            return;
        HeightmapForceTicks += elapsed;
        if (elapsed > HeightmapForceMaxTicks)
            HeightmapForceMaxTicks = elapsed;
    }
}
