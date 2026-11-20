using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AtlasPreloadFix
{
    /// <summary>
    /// AtlasPreloadFix: one global residency pre-pass per static-atlas page, immediately before the
    /// page is packed. Textures that cannot be made resident are removed from the page's input list,
    /// so BuildMeshesForUvs never registers a tile that BlitTexturesToColorAtlas cannot fill and
    /// Graphic.TryGetTextureAtlasReplacementInfo keeps falling back to the source texture.
    ///
    /// This is intentionally not the per-tile approach: there is no tile surgery and no per-block
    /// EnsureGpuResident call inside the blit loop. The decision is made once, on the complete input
    /// set of the page, before the atlas is built.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AtlasPreloadFixMod
    {
        public const string HarmonyId = "sleepsheep.waylith.atlaspreloadfix";
        private const string P = "[ATLASFIX] ";

        private static readonly int BudgetMs = ReadBudget();
        private static bool _installed;
        private static bool _disabled;
        private static double _probeMs;
        private static int _pages;
        private static int _candidates;
        private static int _resident;
        private static int _failed;
        private static int _maskFailed;
        private static int _removed;
        private static int _tiles;
        private static int _budgetPages;
        private static int _allFailPages;

        static AtlasPreloadFixMod()
        {
            try
            {
                Console.Error.WriteLine($"{P}start version=1 budget_ms={BudgetMs} gpu={(GpuConfigured() ? 1 : 0)}");

                var harmony = new Harmony(HarmonyId);
                MethodInfo bake = AccessTools.Method(typeof(StaticTextureAtlas), "Bake", new[] { typeof(bool) });
                if (bake == null)
                {
                    Console.Error.WriteLine($"{P}install FAIL target=StaticTextureAtlas.Bake(bool)");
                    return;
                }

                harmony.Patch(bake,
                    prefix: new HarmonyMethod(typeof(AtlasPreloadFixMod)
                        .GetMethod(nameof(BakePrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(AtlasPreloadFixMod)
                        .GetMethod(nameof(BakePostfix), BindingFlags.Static | BindingFlags.NonPublic)));

                MethodInfo all = AccessTools.Method(typeof(GlobalTextureAtlasManager), "BakeStaticAtlases");
                if (all != null)
                {
                    harmony.Patch(all,
                        postfix: new HarmonyMethod(typeof(AtlasPreloadFixMod)
                            .GetMethod(nameof(BakeAllPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }

                _installed = true;
                Console.Error.WriteLine($"{P}install OK target=StaticTextureAtlas.Bake(bool) summary={(all != null ? 1 : 0)}");

                if (Environment.GetEnvironmentVariable("ATLASFIX_SELFTEST") == "1")
                {
                    AtlasPreloadSelfTest.Run(Console.Error.WriteLine);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{P}install EXC {e.GetType().Name}: {e.Message}");
            }
        }

        private static int ReadBudget()
        {
            string raw = Environment.GetEnvironmentVariable("ATLASFIX_BUDGET_MS");
            if (int.TryParse(raw, out int value) && value >= 0)
            {
                return value;
            }
            return 250;
        }

        private static bool GpuConfigured()
        {
            try
            {
                Type type = AccessTools.TypeByName("Waylith.Rendering.GpuTextureService");
                PropertyInfo property = type?.GetProperty("IsConfigured", BindingFlags.Public | BindingFlags.Static);
                return property != null && (bool)property.GetValue(null);
            }
            catch
            {
                return false;
            }
        }

        private static bool EnsureResident(UnityEngine.Texture2D texture)
        {
            return texture != null && texture.EnsureGpuResident();
        }

        private static void BakePrefix(StaticTextureAtlas __instance)
        {
            if (!_installed || _disabled || __instance == null || __instance.textures == null ||
                __instance.textures.Count == 0)
            {
                return;
            }

            try
            {
                int remaining = BudgetMs <= 0 ? 0 : (int)Math.Max(0, BudgetMs - _probeMs);
                bool hasMask = __instance.groupKey.hasMask;
                AtlasPreloadStats stats = AtlasPreloadPolicy.Apply(
                    __instance.textures, __instance.masks, hasMask, EnsureResident, remaining);

                _pages++;
                _candidates += stats.Candidates;
                _resident += stats.Resident;
                _failed += stats.Failed;
                _maskFailed += stats.MaskFailed;
                _removed += stats.Removed;
                _probeMs += stats.ElapsedMs;
                if (stats.BudgetExhausted)
                {
                    _budgetPages++;
                    _disabled = true;
                }
                if (stats.AllFailed)
                {
                    _allFailPages++;
                }

                Console.Error.WriteLine(
                    $"{P}page group={__instance.groupKey} cand={stats.Candidates} resident={stats.Resident} " +
                    $"fail={stats.Failed} maskfail={stats.MaskFailed} removed={stats.Removed} " +
                    $"ms={stats.ElapsedMs:F2} budget={(stats.BudgetExhausted ? 1 : 0)} " +
                    $"allfail={(stats.AllFailed ? 1 : 0)} left={__instance.textures.Count}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{P}page EXC {e.GetType().Name}: {e.Message}");
            }
        }

        private static void BakePostfix(StaticTextureAtlas __instance)
        {
            if (!_installed || __instance == null)
            {
                return;
            }

            try
            {
                int count = __instance.tiles != null ? __instance.tiles.Count : 0;
                _tiles += count;
                Console.Error.WriteLine(
                    $"{P}tiles group={__instance.groupKey} count={count} textures={__instance.textures.Count}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{P}tiles EXC {e.GetType().Name}: {e.Message}");
            }
        }

        private static void BakeAllPostfix()
        {
            if (!_installed)
            {
                return;
            }

            Console.Error.WriteLine(
                $"{P}summary pages={_pages} cand={_candidates} resident={_resident} fail={_failed} " +
                $"maskfail={_maskFailed} removed={_removed} tiles={_tiles} warmup_ms={_probeMs:F2} " +
                $"budget_pages={_budgetPages} allfail_pages={_allFailPages} disabled={(_disabled ? 1 : 0)}");
        }
    }
}
