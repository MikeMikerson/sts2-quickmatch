using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using QuickMatch.Logging;

namespace QuickMatch;

/// <summary>
/// Mod entry point. The game discovers this class via <see cref="ModInitializerAttribute"/>
/// and calls <see cref="Initialize"/> as a static method before ModelDb is populated.
/// </summary>
[ModInitializer("Initialize")]
public static class MainFile
{
    public const string ModId = "QuickMatch";

    public static ModLogger Logger { get; private set; } = new(ModId);

    public static void Initialize()
    {
        Logger = new ModLogger(ModId);

        // Apply all Harmony patches defined in this assembly (UI, lobby gate, Steam host, localization).
        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());

        Logger.Info("QuickMatch initialized.");
    }
}
