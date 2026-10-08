#!/usr/bin/env python3
"""
analyze_quality.py  ·  Comprobación y resumen del benchmark de CALIDAD.

Uso:
    python analyze_quality.py <carpeta>

<carpeta> puede ser la carpeta de una ejecución (<fecha>_QUALITY_...), que
contiene Q-NEUTRAL/ y/o Q-ABLATION/, o directamente una de esas subcarpetas.
Lee ambos formatos de CSV (',' con decimal '.', o ';' con decimal ',').

Qué hace (sin interpretar automáticamente "mejor" o "peor"):
  1. Valida que cada (configuración, solver) tiene exactamente N mapas con
     run_index 0..N-1 (sin warm-up ni descartes) y que todas las soluciones
     son válidas.
  2. Imprime, por separado para Q-NEUTRAL y Q-ABLATION, media e IC95 de la
     JS global, la JS condicionada y la entropía, y la diversidad media.
  3. Si scipy está disponible, contrasta (Mann-Whitney U bilateral) ThisWork
     frente a cada otro solver en cada configuración para JS global, JS
     condicionada y entropía. La diversidad por mapa no se contrasta porque
     los pares de mapas no son independientes.
"""
import csv
import os
import sys
from collections import defaultdict

try:
    from scipy.stats import mannwhitneyu
except Exception:  # scipy opcional
    mannwhitneyu = None


def read_csv(path):
    with open(path, encoding="utf-8") as f:
        head = f.readline()
    sep = ";" if ";" in head else ","
    dec_comma = sep == ";"
    rows = []
    with open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f, delimiter=sep):
            rows.append(r)
    return rows, dec_comma


def num(v, dec_comma):
    if v is None or v == "":
        return float("nan")
    return float(v.replace(",", ".") if dec_comma else v)


def parts_of(folder):
    if os.path.exists(os.path.join(folder, "quality_maps.csv")):
        return [(os.path.basename(os.path.normpath(folder)), folder)]
    out = []
    for name in ("Q-NEUTRAL", "Q-ABLATION"):
        p = os.path.join(folder, name)
        if os.path.exists(os.path.join(p, "quality_maps.csv")):
            out.append((name, p))
    return out


def analyze(name, folder):
    maps, dc = read_csv(os.path.join(folder, "quality_maps.csv"))
    summ, dc2 = read_csv(os.path.join(folder, "quality_summary.csv"))
    print("=" * 100)
    print(name, "·", folder)
    print("=" * 100)

    # 1. Validación
    groups = defaultdict(list)
    for m in maps:
        groups[(m["config_id"], m["solver"])].append(m)
    n_expected = max(len(v) for v in groups.values()) if groups else 0
    problems = 0
    for (cfg, sv), ms in sorted(groups.items()):
        runs = sorted(int(m["run_index"]) for m in ms)
        if runs != list(range(n_expected)):
            print(f"[FALLO] {cfg} {sv}: run_index = {runs[:5]}... ({len(runs)} mapas)")
            problems += 1
        if any(m["solution_valid"] != "1" for m in ms):
            print(f"[FALLO] {cfg} {sv}: hay soluciones inválidas")
            problems += 1
    benches = sorted(set(m["benchmark"] for m in maps))
    print(f"Validación: {len(groups)} grupos (config, solver), {len(maps)} mapas, "
          f"{n_expected} por grupo, benchmark(s) {benches}, problemas = {problems}")
    if len(benches) > 1:
        print("[AVISO] La carpeta mezcla benchmarks; se esperaban ficheros separados.")
    print()

    # 2. Resumen
    hdr = f"{'config_id':<52}{'solver':<10}{'att/run':>8}{'JS glob [IC95]':>24}{'JS cond [IC95]':>24}{'H (nats)':>9}{'Div':>7}{'Div_und':>8}{'Div_typ':>8}"
    print(hdr)
    print("-" * len(hdr))
    for s in summ:
        f = lambda k: num(s[k], dc2)
        print(f"{s['config_id']:<52}{s['solver']:<10}{f('attempts_mean'):>8.2f}"
              f"{f('js_global_mean'):>8.4f} [{f('js_global_ci95_lo'):.4f},{f('js_global_ci95_hi'):.4f}]"
              f"{f('js_conditional_mean'):>8.4f} [{f('js_conditional_ci95_lo'):.4f},{f('js_conditional_ci95_hi'):.4f}]"
              f"{f('entropy_mean'):>9.3f}{f('diversity_mean'):>7.3f}{f('diversity_undecided_mean'):>8.3f}{f('diversity_undecided_type_mean'):>8.3f}")
    print()

    # 3. Contrastes (descriptivos de diferencia, no de calidad)
    if mannwhitneyu is None:
        print("(scipy no disponible: se omiten los contrastes)")
        return problems
    print("Mann-Whitney U bilateral, ThisWork frente a otro solver (p sin corregir por comparaciones múltiples)")
    by_cfg = defaultdict(dict)
    for (cfg, sv), ms in groups.items():
        by_cfg[cfg][sv] = ms
    for cfg in sorted(by_cfg):
        d = by_cfg[cfg]
        if "ThisWork" not in d:
            continue
        for other in sorted(k for k in d if k != "ThisWork"):
            cells = []
            for metric in ("js_global", "js_conditional", "entropy"):
                a = [num(m[metric], dc) for m in d["ThisWork"]]
                b = [num(m[metric], dc) for m in d[other]]
                p = mannwhitneyu(a, b, alternative="two-sided").pvalue
                cells.append(f"{metric}: p={p:.3g}")
            print(f"  {cfg:<52} vs {other:<10} " + "  ".join(cells))
    print()
    return problems


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    total = 0
    parts = parts_of(sys.argv[1])
    if not parts:
        print("No se encuentra quality_maps.csv en la carpeta indicada ni en Q-NEUTRAL/ o Q-ABLATION/.")
        sys.exit(1)
    for name, folder in parts:
        total += analyze(name, folder)
    sys.exit(1 if total else 0)


if __name__ == "__main__":
    main()
