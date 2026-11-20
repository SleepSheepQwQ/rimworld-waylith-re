using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace AtlasPreloadFix
{
    /// <summary>
    /// Removes textures that cannot be made GPU-resident from a static atlas before the atlas is
    /// packed. The rule is deliberately a whole-page pre-pass, not a per-tile repair: a texture that
    /// fails here is never registered in <c>StaticTextureAtlas.tiles</c>, so
    /// <c>Graphic.TryGetTextureAtlasReplacementInfo</c> keeps using the source texture instead of
    /// swapping a blank atlas tile over it.
    ///
    /// The method has no Unity dependency beyond the texture objects, which lets the no-GPU probe
    /// run it with an injected residency predicate.
    /// </summary>
    public static class AtlasPreloadPolicy
    {
        /// <summary>
        /// Filter <paramref name="textures"/> in place.
        /// <paramref name="ensureResident"/> is normally <c>t =&gt; t.EnsureGpuResident()</c>.
        /// <paramref name="budgetMs"/> is the remaining startup budget in milliseconds; 0 or less
        /// means "no cap". If the budget runs out, no texture is removed from this page at all
        /// (a half-selected page would otherwise mix probed and unprobed textures) and the caller is
        /// expected to stop probing later pages.
        /// </summary>
        public static AtlasPreloadStats Apply(
            List<Texture2D> textures,
            Dictionary<Texture2D, Texture2D> masks,
            bool hasMask,
            Func<Texture2D, bool> ensureResident,
            int budgetMs)
        {
            var stats = new AtlasPreloadStats();
            if (textures == null || textures.Count == 0)
            {
                return stats;
            }

            stats.Candidates = textures.Count;
            var clock = Stopwatch.StartNew();
            var failures = new List<Texture2D>();

            for (int i = 0; i < textures.Count; i++)
            {
                if (budgetMs > 0 && clock.ElapsedMilliseconds >= budgetMs)
                {
                    stats.BudgetExhausted = true;
                    stats.ElapsedMs = clock.Elapsed.TotalMilliseconds;
                    return stats;
                }

                Texture2D texture = textures[i];
                bool colorResident;
                try
                {
                    colorResident = texture != null && ensureResident(texture);
                }
                catch
                {
                    colorResident = false;
                }

                if (!colorResident)
                {
                    failures.Add(texture);
                    continue;
                }

                if (hasMask)
                {
                    stats.MaskChecked++;
                    Texture2D mask = null;
                    if (masks != null)
                    {
                        masks.TryGetValue(texture, out mask);
                    }

                    bool maskResident;
                    try
                    {
                        maskResident = mask != null && ensureResident(mask);
                    }
                    catch
                    {
                        maskResident = false;
                    }

                    if (!maskResident)
                    {
                        stats.MaskFailed++;
                        failures.Add(texture);
                        continue;
                    }
                }

                stats.Resident++;
            }

            stats.ElapsedMs = clock.Elapsed.TotalMilliseconds;
            stats.Failed = failures.Count;

            if (failures.Count == 0)
            {
                return stats;
            }

            if (stats.Resident == 0)
            {
                // No candidate could be made resident. That is what a host with no GPU service looks
                // like (the headless CI host), and also what a genuinely dead page looks like.
                // Removing the whole page would turn "atlas has blank tiles" into "atlas vanished";
                // leave the shipped behaviour alone in that case and let the caller report it.
                stats.AllFailed = true;
                return stats;
            }

            foreach (Texture2D failed in failures)
            {
                textures.Remove(failed);
                if (failed != null && masks != null)
                {
                    masks.Remove(failed);
                }
            }

            stats.Removed = failures.Count;
            return stats;
        }
    }

    public sealed class AtlasPreloadStats
    {
        public int Candidates;
        public int Resident;
        public int MaskChecked;
        public int MaskFailed;
        public int Failed;
        public int Removed;
        public bool AllFailed;
        public bool BudgetExhausted;
        public double ElapsedMs;
    }
}
