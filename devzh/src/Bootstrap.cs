using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DevModeZh
{
    /// <summary>
    /// Translates the developer-mode action labels. The labels are hard-coded English strings in
    /// Assembly-CSharp (DebugActionAttribute names, literal DebugActionNode labels and category
    /// constants), so nothing in the game's language packs can reach them. We translate at the two
    /// points where the labels become visible:
    ///   - Verse.DebugActionNode::get_LabelNow  - every action, submenu entry and tool name
    ///   - Verse.Dialog_Debug::DoLabel(string,float) - the category headers
    /// Everything else (tab names) is a DefInjected label and needs no code.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string LogPrefix = "[DevModeZh] ";

        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("sleepsheep.waylith.devzh");

                var labelNow = AccessTools.Method("Verse.DebugActionNode:get_LabelNow");
                var doLabel = AccessTools.Method("Verse.Dialog_Debug:DoLabel", new[] { typeof(string), typeof(float) });

                if (labelNow == null)
                {
                    Log.Warning(LogPrefix + "Verse.DebugActionNode:get_LabelNow not found; no action labels translated.");
                }
                else
                {
                    harmony.Patch(labelNow, postfix: new HarmonyMethod(typeof(Bootstrap), nameof(LabelNowPostfix)));
                }

                if (doLabel == null)
                {
                    Log.Warning(LogPrefix + "Verse.Dialog_Debug:DoLabel(string,float) not found; category headers stay English.");
                }
                else
                {
                    harmony.Patch(doLabel, postfix: new HarmonyMethod(typeof(Bootstrap), nameof(DoLabelPostfix)));
                }

                Log.Message(LogPrefix + "installed; " + Dict.Map.Count + " labels mapped (devmodezh 1.0.0)");
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "failed to install: " + e);
            }
        }

        private static void LabelNowPostfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result))
            {
                return;
            }

            if (Dict.Map.TryGetValue(__result, out var zh))
            {
                __result = zh;
                return;
            }

            // Dynamic labels such as "Wave 3" or "Level 7" are built from a translated prefix and a
            // number; translate the prefix so the line still reads Chinese.
            foreach (var kv in PrefixMap.Prefixes)
            {
                if (__result.StartsWith(kv.Key, StringComparison.Ordinal))
                {
                    __result = kv.Value + __result.Substring(kv.Key.Length);
                    return;
                }
            }
        }

        private static void DoLabelPostfix(ref string label)
        {
            if (!string.IsNullOrEmpty(label) && Dict.Map.TryGetValue(label, out var zh))
            {
                label = zh;
            }
        }
    }

    /// <summary>Labels that carry a trailing number or value and therefore never match exactly.</summary>
    internal static class PrefixMap
    {
        internal static readonly List<KeyValuePair<string, string>> Prefixes = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Wave ", "波次 "),
            new KeyValuePair<string, string>("Level ", "等级 "),
            new KeyValuePair<string, string>("*Current (times called: ", "*当前（调用次数："),
        };
    }
}
