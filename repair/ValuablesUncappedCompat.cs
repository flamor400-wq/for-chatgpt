using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Empress.ValuablesUncapped
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class EmpressValuablesUncappedPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "empress.repo.valuablesbypass";
        public const string PluginName = "Empress Valuables Uncapped (REPO 0.5.1 Compatibility)";
        public const string PluginVersion = "0.5.1.1";

        internal static ConfigEntry<bool> BypassValueBudget = null!;
        internal static ConfigEntry<bool> FillValuableSpots = null!;
        internal static ConfigEntry<bool> HostOnly = null!;
        internal static ManualLogSource ModLog = null!;

        private Harmony? _harmony;

        private void Awake()
        {
            ModLog = Logger;
            BypassValueBudget = Config.Bind(
                "General",
                "BypassValueBudget",
                true,
                "Ignores the total value budget so expensive valuables do not reduce the spawn count.");

            FillValuableSpots = Config.Bind(
                "General",
                "FillValuableSpots",
                true,
                "Uses most valid valuable spots instead of vanilla per-size and total spawn caps.");

            HostOnly = Config.Bind(
                "General",
                "HostOnly",
                true,
                "Applies spawning changes only on the host/master client.");

            try
            {
                Type? directorType = AccessTools.TypeByName("ValuableDirector");
                if (directorType == null)
                    throw new TypeLoadException("ValuableDirector was not found in the loaded game assemblies.");

                Type? prefabRefType = AccessTools.TypeByName("PrefabRef");
                Type? valuableVolumeType = AccessTools.TypeByName("ValuableVolume");
                if (prefabRefType == null || valuableVolumeType == null)
                    throw new TypeLoadException("PrefabRef or ValuableVolume was not found.");

                MethodInfo? spawnMethod = FindSpawnMethod(directorType, prefabRefType, valuableVolumeType);
                if (spawnMethod == null)
                    throw new MissingMethodException("Could not find a supported ValuableDirector.SpawnValuable overload.");

                MethodInfo? setupHostMethod = AccessTools.Method(directorType, "SetupHost", Type.EmptyTypes);
                if (setupHostMethod == null)
                    throw new MissingMethodException("Could not find ValuableDirector.SetupHost().");

                _harmony = new Harmony(PluginGuid);
                MethodInfo spawnPrefix = typeof(ValuableDirectorPatches).GetMethod(
                    nameof(ValuableDirectorPatches.SpawnValuablePrefix),
                    BindingFlags.Public | BindingFlags.Static)!;
                MethodInfo setupPrefix = typeof(ValuableDirectorPatches).GetMethod(
                    nameof(ValuableDirectorPatches.SetupHostPrefix),
                    BindingFlags.Public | BindingFlags.Static)!;

                _harmony.Patch(spawnMethod, prefix: new HarmonyMethod(spawnPrefix));
                _harmony.Patch(setupHostMethod, prefix: new HarmonyMethod(setupPrefix));

                ModLog.LogInfo("Compatible ValuableDirector patches installed.");
                ModLog.LogInfo("Spawn method: " + FormatMethod(spawnMethod));
                ModLog.LogInfo("SetupHost method: " + FormatMethod(setupHostMethod));
                ModLog.LogInfo("BypassValueBudget=" + BypassValueBudget.Value
                    + ", FillValuableSpots=" + FillValuableSpots.Value
                    + ", HostOnly=" + HostOnly.Value);
            }
            catch (Exception ex)
            {
                ModLog.LogError("Failed to initialize the 0.5.1 compatibility patches: " + ex);
            }
        }

        private static MethodInfo? FindSpawnMethod(Type directorType, Type prefabRefType, Type volumeType)
        {
            MethodInfo? exact = AccessTools.Method(
                directorType,
                "SpawnValuable",
                new[] { prefabRefType, volumeType });
            if (exact != null)
                return exact;

            // Defensive fallback for a future build that adds an optional label argument.
            return directorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "SpawnValuable")
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 3
                        && parameters[0].ParameterType == prefabRefType
                        && parameters[1].ParameterType == volumeType
                        && parameters[2].ParameterType == typeof(string);
                });
        }

        private static string FormatMethod(MethodInfo method)
        {
            return method.Name + "(" + string.Join(", ", method.GetParameters()
                .Select(parameter => parameter.ParameterType.Name)) + ")";
        }
    }

    internal static class ValuableDirectorPatches
    {
        public static void SpawnValuablePrefix(object __instance)
        {
            if (!EmpressValuablesUncappedPlugin.BypassValueBudget.Value || !ShouldApply())
                return;

            try
            {
                FieldInfo? totalMaxValue = AccessTools.Field(__instance.GetType(), "totalMaxValue");
                if (totalMaxValue == null)
                {
                    EmpressValuablesUncappedPlugin.ModLog.LogError(
                        "Could not find ValuableDirector.totalMaxValue; value-budget bypass was skipped.");
                    return;
                }

                totalMaxValue.SetValue(__instance, float.PositiveInfinity);
            }
            catch (Exception ex)
            {
                EmpressValuablesUncappedPlugin.ModLog.LogError("Failed to bypass valuable value budget: " + ex);
            }
        }

        public static void SetupHostPrefix(object __instance)
        {
            if (!EmpressValuablesUncappedPlugin.FillValuableSpots.Value || !ShouldApply())
                return;

            try
            {
                FieldInfo? valuableDebug = AccessTools.Field(__instance.GetType(), "valuableDebug");
                if (valuableDebug == null || !valuableDebug.FieldType.IsEnum)
                {
                    EmpressValuablesUncappedPlugin.ModLog.LogError(
                        "Could not find the ValuableDirector.valuableDebug enum field; spot filling was skipped.");
                    return;
                }

                // ValuableDirector.ValuableDebug.All is enum value 1 in REPO 0.5.1.
                valuableDebug.SetValue(__instance, Enum.ToObject(valuableDebug.FieldType, 1));
            }
            catch (Exception ex)
            {
                EmpressValuablesUncappedPlugin.ModLog.LogError("Failed to enable valuable spot filling: " + ex);
            }
        }

        private static bool ShouldApply()
        {
            if (!EmpressValuablesUncappedPlugin.HostOnly.Value)
                return true;

            try
            {
                Type? semiFunc = AccessTools.TypeByName("SemiFunc");
                MethodInfo? hostCheck = semiFunc == null
                    ? null
                    : AccessTools.Method(semiFunc, "IsMasterClientOrSingleplayer", Type.EmptyTypes);

                if (hostCheck != null)
                    return Convert.ToBoolean(hostCheck.Invoke(null, null));

                // Fallback: in a Photon room only the master applies the change;
                // outside a room this is single-player and therefore the local host.
                Type? photonNetwork = AccessTools.TypeByName("Photon.Pun.PhotonNetwork");
                PropertyInfo? inRoomProperty = photonNetwork == null ? null : AccessTools.Property(photonNetwork, "InRoom");
                PropertyInfo? masterProperty = photonNetwork == null ? null : AccessTools.Property(photonNetwork, "IsMasterClient");

                if (inRoomProperty == null || masterProperty == null)
                {
                    EmpressValuablesUncappedPlugin.ModLog.LogError(
                        "Could not determine host state; host-only patch was safely skipped.");
                    return false;
                }

                bool inRoom = Convert.ToBoolean(inRoomProperty.GetValue(null));
                return !inRoom || Convert.ToBoolean(masterProperty.GetValue(null));
            }
            catch (Exception ex)
            {
                EmpressValuablesUncappedPlugin.ModLog.LogError(
                    "Host check failed; host-only patch was safely skipped: " + ex.Message);
                return false;
            }
        }
    }
}
