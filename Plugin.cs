using System;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using OttoBifrost.Patches;
using ServerSync;
using UnityEngine;

namespace OttoBifrost;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class OttoBifrostPlugin : BaseUnityPlugin
{
    internal const string ModName = "OttoBifrost";
    internal const string ModVersion = "1.2.0";
    internal const string Author = "potto007";
    internal const string ModGUID = $"{Author}.{ModName}";

    internal static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private readonly Harmony _harmony = new(ModGUID);

    private static readonly ConfigSync ConfigSync = new(ModGUID)
        { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

    private const string GeneralSection = ModName;
    private const string PreviewSection = "Preview";
    private const string DebugSection = "Debug";
    private const string ConfigFileName = $"{ModGUID}.cfg";

    // v1.2.0 made StaticView the default, but BepInEx had already written LiveView into every
    // config file an earlier version saved. Those files move to StaticView once: the header names
    // the version that last saved the file, and the first save by this version rewrites it.
    // assembly_valheim declares its own global Version type.
    private static readonly System.Version StaticViewDefaultSince = new(1, 2, 0);
    private static readonly Regex SavedByHeader = new(@"^## Settings file was created by plugin .+ v(\d+(?:\.\d+){1,3})");

    internal enum PreviewModes
    {
        StaticView,
        LiveView
    }

    internal static ConfigEntry<bool> LockConfiguration = null!;
    internal static ConfigEntry<bool> PreloadDestinations = null!;
    internal static ConfigEntry<bool> FastTeleport = null!;
    internal static ConfigEntry<PreviewModes> PreviewMode = null!;
    internal static ConfigEntry<bool> LogPerformance = null!;

    private FileSystemWatcher? _configWatcher;

    private void Awake()
    {
        BindConfig();
        _harmony.PatchAll(typeof(OttoBifrostPlugin).Assembly);
        WatchConfigFile();
        Log.LogInfo($"{ModName} {ModVersion} loaded.");
    }

    private void Update()
    {
        PerfStats.Tick(Time.unscaledDeltaTime, LogPerformance.Value);
        Destinations.Tick();
        DestinationSync.ClientUpdate();
    }

    private void OnDestroy()
    {
        _configWatcher?.Dispose();
        _harmony.UnpatchSelf();
    }

    private void BindConfig()
    {
        // Read before the first Bind, which saves the file under this version's header.
        System.Version? savedBy = ReadSavedByVersion();

        LockConfiguration = BindSynced(GeneralSection, "LockConfiguration", true, "If on, only server admins can change the synced settings.");
        _ = ConfigSync.AddLockingConfigEntry(LockConfiguration);
        PreloadDestinations = BindSynced(GeneralSection, "PreloadDestinations", true, "Load the area around a portal's destination while you stand near the portal.");
        FastTeleport = BindSynced(GeneralSection, "FastTeleport", true, "Skip the vanilla wait when the destination is already loaded.");
        PreviewMode = Config.Bind(PreviewSection, "PreviewMode", PreviewModes.StaticView,
            "StaticView shows one picture of the destination, taken once its objects are built, and renders nothing after that. " +
            "LiveView renders the nearest portal's destination continuously and follows your head. Not synced.");
        LogPerformance = Config.Bind(DebugSection, "LogPerformance", false, "Write a timing summary to the log every 5 seconds. Leave off in normal play. Not synced.");
        MigratePreviewMode(savedBy);
    }

    /// Null when the file does not exist yet or its header does not name a version.
    private System.Version? ReadSavedByVersion()
    {
        try
        {
            if (!File.Exists(Config.ConfigFilePath))
                return null;
            using StreamReader reader = new(Config.ConfigFilePath);
            Match match = SavedByHeader.Match(reader.ReadLine() ?? string.Empty);
            return match.Success ? new System.Version(match.Groups[1].Value) : null;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Could not read the version header of {ConfigFileName}: {ex.Message}");
            return null;
        }
    }

    private static void MigratePreviewMode(System.Version? savedBy)
    {
        if (savedBy == null || savedBy >= StaticViewDefaultSince || PreviewMode.Value != PreviewModes.LiveView)
            return;

        PreviewMode.Value = PreviewModes.StaticView;
        Log.LogInfo($"PreviewMode changed from LiveView to StaticView, the default since v{StaticViewDefaultSince.ToString(3)}. {ConfigFileName} was last saved by v{savedBy}.");
    }

    private ConfigEntry<T> BindSynced<T>(string section, string key, T value, string description)
    {
        ConfigEntry<T> entry = Config.Bind(section, key, value, $"{description} [Synced with Server]");
        ConfigSync.AddConfigEntry(entry).SynchronizedConfig = true;
        return entry;
    }

    /// BepInEx reads the config file only at load, so hand edits need a watcher to apply
    /// while the game runs.
    private void WatchConfigFile()
    {
        _configWatcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName)
        {
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        _configWatcher.Renamed += OnConfigFileChanged;
        _configWatcher.EnableRaisingEvents = true;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!File.Exists(Path.Combine(Paths.ConfigPath, ConfigFileName)))
            return;
        try
        {
            Config.Reload();
        }
        catch (Exception ex)
        {
            Log.LogError($"Could not reload {ConfigFileName}. Check the entries for spelling and format: {ex.Message}");
        }
    }
}
