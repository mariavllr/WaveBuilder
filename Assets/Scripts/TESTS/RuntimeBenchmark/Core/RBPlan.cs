// ============================================================================
// RBPlan.cs  (construcción de la matriz de configuraciones A y B)
//
// Benchmark A  (A_UNRESTRICTED): ThisWork, Gumin, DeBroglie
//              × tilesets × tamaños. Mismo dominio, mismas variantes, mismos
//              pesos y mismas adyacencias (sin negative rules), sin celdas
//              preasignadas, bordes del volumen sin restricción.
// Benchmark B  (B0..B4): ThisWork, DeBroglie × tilesets × tamaños × niveles.
//              B0 es la misma configuración que A (réplica con las mismas semillas).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WFCRuntimeBenchmark
{
    public sealed class TilesetPair
    {
        public string Name;
        public CompiledTileset Plain;      // adyacencias sin negative rules
        public CompiledTileset Negative;   // adyacencias con negative rules
    }

    public sealed class PlanOptions
    {
        public bool RunA = true;
        public bool RunB = true;
        public bool[] BLevels = { true, true, true, true, true };
        public ThisWorkSelector ThisWorkSelector = ThisWorkSelector.Heap;
        public bool IncludeThisWorkAlternativeSelector = false;   // añade la otra variante del selector
        public bool IncludeDeBroglieNative = false;               // añade DeBroglie "native" en B1..B4
        public bool IncludeDeBroglieLazyInit = false;             // añade DeBroglie sin propagación inicial (comportamiento por defecto de la librería)
    }

    public static class BenchmarkPlan
    {
        public static List<ConfigGroup> Build(List<TilesetPair> tilesets, List<int[]> sizes, PlanOptions o)
        {
            var groups = new List<ConfigGroup>();
            ThisWorkSelector main = o.ThisWorkSelector;
            ThisWorkSelector alt = main == ThisWorkSelector.Heap ? ThisWorkSelector.Linear : ThisWorkSelector.Heap;

            if (o.RunA)
                foreach (var ts in tilesets)
                    foreach (var sz in sizes)
                    {
                        var g = NewGroup(LevelSpec.A_Unrestricted(), ts, sz);
                        g.Solvers.Add(() => new ThisWorkSolver(main));
                        if (o.IncludeThisWorkAlternativeSelector) g.Solvers.Add(() => new ThisWorkSolver(alt));
                        g.Solvers.Add(() => new GuminSolver());
                        g.Solvers.Add(() => new DeBroglieSolver(DeBroglieMode.Matched));
                        if (o.IncludeDeBroglieLazyInit) g.Solvers.Add(() => new DeBroglieSolver(DeBroglieMode.Matched, false));
                        groups.Add(g);
                    }

            if (o.RunB)
                foreach (var ts in tilesets)
                    foreach (var sz in sizes)
                        for (int lv = 0; lv <= 4; lv++)
                        {
                            if (!o.BLevels[lv]) continue;
                            var g = NewGroup(LevelSpec.B(lv), ts, sz);
                            g.Solvers.Add(() => new ThisWorkSolver(main));
                            if (o.IncludeThisWorkAlternativeSelector) g.Solvers.Add(() => new ThisWorkSolver(alt));
                            g.Solvers.Add(() => new DeBroglieSolver(DeBroglieMode.Matched));
                            if (o.IncludeDeBroglieLazyInit) g.Solvers.Add(() => new DeBroglieSolver(DeBroglieMode.Matched, false));
                            if (o.IncludeDeBroglieNative && lv > 0) g.Solvers.Add(() => new DeBroglieSolver(DeBroglieMode.Native));
                            groups.Add(g);
                        }
            return groups;
        }

        private static ConfigGroup NewGroup(LevelSpec lv, TilesetPair ts, int[] sz)
        {
            return new ConfigGroup
            {
                Level = lv, TilesetName = ts.Name, TilesetPlain = ts.Plain, TilesetNegative = ts.Negative,
                MX = sz[0], MY = sz[1], MZ = sz[2],
            };
        }

        /// <summary>Descripción textual (manifest) de los tilesets compilados y de la matriz.</summary>
        public static string Describe(List<TilesetPair> tilesets, List<ConfigGroup> groups, BenchmarkSettings s, PlanOptions o)
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("## Settings");
            sb.AppendLine("runs_per_config=" + s.Runs);
            sb.AppendLine("warmup_runs_per_config=" + s.WarmupRuns + " (registrados con phase=warmup, excluidos del resumen)");
            sb.AppendLine("max_attempts_per_run=" + s.MaxAttemptsPerRun);
            sb.AppendLine("base_seed=" + s.BaseSeed);
            sb.AppendLine("gc_before_timed_phases=" + s.GcBeforeTimedPhases);
            sb.AppendLine("interleave_solvers=" + s.InterleaveSolvers);
            sb.AppendLine("this_work_selector=" + o.ThisWorkSelector + " | alt_selector=" + o.IncludeThisWorkAlternativeSelector + " | debroglie_native=" + o.IncludeDeBroglieNative + " | debroglie_lazyinit=" + o.IncludeDeBroglieLazyInit);
            sb.AppendLine("stopwatch_frequency_hz=" + System.Diagnostics.Stopwatch.Frequency + " high_resolution=" + System.Diagnostics.Stopwatch.IsHighResolution);
            sb.AppendLine("timed_window=t_init (reset del estado + restricciones de la instancia + propagación inicial) + t_search (observación-colapso-propagación), sumado sobre todos los intentos del run");
            sb.AppendLine();
            sb.AppendLine("## Tilesets");
            foreach (var t in tilesets)
                foreach (var c in new[] { t.Plain, t.Negative })
                {
                    var baseSet = new HashSet<int>(c.BaseTile);
                    sb.AppendLine(string.Format(ci,
                        "{0} negative_rules={1}: tiles_total={2} base_tiles={3} generated_variants={4} domain_T={5} directed_relations_all={6} directed_relations_domain={7} asymmetric_relations={8} nonpositive_weight_tiles={9} floor={10} empty={11} limit={12} fixed_specs={13}",
                        t.Name, c.NegativeRules, c.TileCount, baseSet.Count, c.TileCount - baseSet.Count, c.T, c.DirectedRelations, c.DomainDirectedRelations,
                        c.AsymmetricRelations, c.NonPositiveWeightTiles,
                        c.FloorTile >= 0 ? c.TileNames[c.FloorTile] : "-", c.EmptyTile >= 0 ? c.TileNames[c.EmptyTile] : "-",
                        c.LimitTile >= 0 ? c.TileNames[c.LimitTile] : "-", DescribeFixed(c)));
                }
            sb.AppendLine();
            sb.AppendLine("## Configurations (" + groups.Count + ")");
            foreach (var g in groups)
            {
                var names = new List<string>();
                foreach (var f in g.Solvers) { var sv = f(); if (sv.Supports(g.Level)) names.Add(sv.Name + "/" + sv.Variant); }
                sb.AppendLine(g.ConfigId + " | solvers=" + string.Join(";", names.ToArray()));
            }
            return sb.ToString();
        }

        private static string DescribeFixed(CompiledTileset c)
        {
            if (c.FixedTiles.Count == 0) return "none";
            var parts = new List<string>();
            foreach (var f in c.FixedTiles) parts.Add(c.TileNames[f.BaseTile] + "x" + f.Count + (f.Layer >= 0 ? "@y" + f.Layer : "") + "(" + f.Variants.Length + " rot)");
            return string.Join(";", parts.ToArray());
        }
    }
}
