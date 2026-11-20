using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;

namespace AtlasPreloadFix
{
    /// <summary>
    /// Self-test for the pre-bake selection. It runs on the real shipped types with no GPU:
    /// one texture is forced to look resident, the other is left with no GPU resource, and the test
    /// asserts that the policy keeps the first in the atlas tile map and drops the second. A control
    /// atlas without the policy must contain both (otherwise the assertion would be vacuous).
    /// </summary>
    public static class AtlasPreloadSelfTest
    {
        private const string GoodName = "AtlasPreload_Good";
        private const string BadName = "AtlasPreload_Bad";

        public static int Run(Action<string> log)
        {
            int failures = 0;
            log("[ATLASFIX] selftest begin");

            bool forced = false;
            try
            {
                var good = NewTexture(GoodName);
                var bad = NewTexture(BadName);
                forced = TryForceResident(good, log);

                var atlas = NewAtlas();
                atlas.Insert(good, null);
                atlas.Insert(bad, null);

                Func<Texture2D, bool> ensure = forced
                    ? (Func<Texture2D, bool>)(t => t.EnsureGpuResident())
                    : (t => t.name == GoodName || t.EnsureGpuResident());

                AtlasPreloadStats stats = AtlasPreloadPolicy.Apply(
                    atlas.textures, atlas.masks, hasMask: false, ensureResident: ensure, budgetMs: 2000);

                bool goodStillListed = atlas.textures.Contains(good);
                bool badStillListed = atlas.textures.Contains(bad);

                var rects = new[]
                {
                    new Rect(0f, 0f, 0.5f, 1f),
                    new Rect(0.5f, 0f, 0.5f, 1f),
                };
                Rect[] use = atlas.textures.Count == 1 ? new[] { rects[0] } : rects;
                atlas.BuildMeshesForUvs(use);

                bool goodTile = atlas.TryGetTile(good, out _);
                bool badTile = atlas.TryGetTile(bad, out _);

                failures += Check(log, "poison_removed", !badStillListed && !badTile,
                    $"bad_listed={(badStillListed ? 1 : 0)} bad_tile={(badTile ? 1 : 0)} removed={stats.Removed}");
                failures += Check(log, "good_kept", goodStillListed && goodTile,
                    $"good_listed={(goodStillListed ? 1 : 0)} good_tile={(goodTile ? 1 : 0)} resident={stats.Resident}");
                failures += Check(log, "counts", stats.Candidates == 2 && stats.Removed == 1 && stats.Resident == 1 && !stats.AllFailed,
                    $"cand={stats.Candidates} resident={stats.Resident} removed={stats.Removed} allfail={(stats.AllFailed ? 1 : 0)}");

                // Control: the same two textures registered without the policy. Both get tiles; if
                // this ever stops being true the poison assertion above is meaningless.
                var control = NewAtlas();
                var good2 = NewTexture(GoodName + "_control");
                var bad2 = NewTexture(BadName + "_control");
                control.Insert(good2, null);
                control.Insert(bad2, null);
                control.BuildMeshesForUvs(rects);
                bool controlBadTile = control.TryGetTile(bad2, out _);
                bool controlGoodTile = control.TryGetTile(good2, out _);
                failures += Check(log, "control_bad_registered", controlBadTile && controlGoodTile,
                    $"good_tile={(controlGoodTile ? 1 : 0)} bad_tile={(controlBadTile ? 1 : 0)}");

                log($"[ATLASFIX] selftest fake_resident={(forced ? 1 : 0)}");
            }
            catch (Exception e)
            {
                log($"[ATLASFIX] selftest case=exception FAIL {e.GetType().Name}: {e.Message}");
                failures++;
            }

            // Guard: a page where every probe fails must be left untouched. This is the headless
            // host's shape (no GpuTextureService at all) and the reason the patch cannot strip an
            // entire atlas merely because the probe is unavailable.
            try
            {
                var atlas = NewAtlas();
                var a = NewTexture("AtlasPreload_AllFail_A");
                var b = NewTexture("AtlasPreload_AllFail_B");
                atlas.Insert(a, null);
                atlas.Insert(b, null);
                AtlasPreloadStats stats = AtlasPreloadPolicy.Apply(
                    atlas.textures, atlas.masks, hasMask: false, ensureResident: t => false, budgetMs: 2000);
                atlas.BuildMeshesForUvs(new[]
                {
                    new Rect(0f, 0f, 0.5f, 1f),
                    new Rect(0.5f, 0f, 0.5f, 1f),
                });
                bool tiles = atlas.tiles.Count == 2;
                failures += Check(log, "all_fail_guard", stats.AllFailed && stats.Removed == 0 && tiles,
                    $"allfail={(stats.AllFailed ? 1 : 0)} removed={stats.Removed} tiles={atlas.tiles.Count}");
            }
            catch (Exception e)
            {
                log($"[ATLASFIX] selftest case=all_fail_guard FAIL {e.GetType().Name}: {e.Message}");
                failures++;
            }

            // Budget guard: a probe that is too slow must be abandoned before anything is removed.
            try
            {
                var atlas = NewAtlas();
                var a = NewTexture("AtlasPreload_Slow_A");
                var b = NewTexture("AtlasPreload_Slow_B");
                atlas.Insert(a, null);
                atlas.Insert(b, null);
                AtlasPreloadStats stats = AtlasPreloadPolicy.Apply(
                    atlas.textures, atlas.masks, hasMask: false,
                    ensureResident: t => { System.Threading.Thread.Sleep(25); return false; },
                    budgetMs: 1);
                bool untouched = atlas.textures.Count == 2 && stats.Removed == 0 && stats.BudgetExhausted;
                failures += Check(log, "budget_guard", untouched,
                    $"budget={(stats.BudgetExhausted ? 1 : 0)} removed={stats.Removed} left={atlas.textures.Count} ms={stats.ElapsedMs:F1}");
            }
            catch (Exception e)
            {
                log($"[ATLASFIX] selftest case=budget_guard FAIL {e.GetType().Name}: {e.Message}");
                failures++;
            }

            log($"[ATLASFIX] selftest result={(failures == 0 ? "PASS" : "FAIL")} fail={failures}");
            return failures;
        }

        private static StaticTextureAtlas NewAtlas()
        {
            return new StaticTextureAtlas(new TextureAtlasGroupKey
            {
                group = TextureAtlasGroup.Misc,
                hasMask = false,
            });
        }

        private static Texture2D NewTexture(string name)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false);
            texture.name = name;
            return texture;
        }

        /// <summary>
        /// Headless has no GpuTextureService, so a texture constructed here has GpuResource == null.
        /// Give one texture a sentinel GpuTextureAllocation so EnsureResident() returns true through
        /// the shipped first branch. Reflection only: the mod does not compile against Waylith.dll.
        /// </summary>
        private static bool TryForceResident(Texture2D texture, Action<string> log)
        {
            try
            {
                object core = typeof(Texture2D)
                    .GetProperty("CoreTexture", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(texture);
                if (core == null)
                {
                    return false;
                }

                PropertyInfo property = core.GetType().GetProperty(
                    "GpuResource", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType == null)
                {
                    return false;
                }

                object sentinel = System.Runtime.CompilerServices.RuntimeHelpers
                    .GetUninitializedObject(property.PropertyType);
                property.SetValue(core, sentinel);
                return texture.EnsureGpuResident();
            }
            catch (Exception e)
            {
                log($"[ATLASFIX] selftest force_resident EXC {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static int Check(Action<string> log, string name, bool ok, string detail)
        {
            log($"[ATLASFIX] selftest case={name} {(ok ? "PASS" : "FAIL")} {detail}");
            return ok ? 0 : 1;
        }
    }
}
