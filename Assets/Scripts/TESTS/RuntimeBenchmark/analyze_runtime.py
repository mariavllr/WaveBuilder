#!/usr/bin/env python3
"""
analyze_runtime.py: tablas rápidas a partir de summary.csv del benchmark de runtime.

Uso:
    python analyze_runtime.py <carpeta_de_resultados> [--md salida.md]

Solo usa la biblioteca estándar. Genera:
  · Tabla A (A_UNRESTRICTED): mediana [IQR], media, éxito en el primer intento (k/n),
    contradicciones, celdas libres y µs por celda libre.
  · Tabla B (B0..B4): por tileset y tamaño, coste de cada nivel y su incremento
    respecto al nivel anterior (Δ mediana en ms y en %).
"""
import csv
import os
import sys
from collections import defaultdict


def num(x):
    """Convierte a float aceptando decimal '.' o ','."""
    return float(str(x).replace(",", "."))


def f(x, d=2):
    try:
        return f"{num(x):.{d}f}"
    except (TypeError, ValueError):
        return "-"


def load(folder):
    """Lee summary.csv con separador ',' (estándar) o ';' (Excel español)."""
    with open(os.path.join(folder, "summary.csv"), newline="", encoding="utf-8") as fh:
        first = fh.readline()
        fh.seek(0)
        sep = ";" if first.count(";") > first.count(",") else ","
        return list(csv.DictReader(fh, delimiter=sep))


def solver_label(r):
    return r["solver"] if r["solver"] == "Gumin" else f"{r['solver']}/{r['solver_variant']}"


def table_a(rows):
    out = ["## Benchmark A: UNRESTRICTED (mismo problema WFC para los tres solvers)", "",
           "| Tileset | Tamaño | Solver | Me (ms) | IQR | Media (ms) | Éxito 1er intento | Contr. | Celdas libres | µs/celda libre (Me) |",
           "|---|---|---|---|---|---|---|---|---|---|"]
    for r in sorted((r for r in rows if r["benchmark"] == "A"),
                    key=lambda r: (r["tileset"], int(r["total_cells"]), solver_label(r))):
        k = round(num(r["success_rate_first_attempt"]) * int(r["n_runs"]))
        out.append(f"| {r['tileset']} | {r['size_label']} | {solver_label(r)} | {f(r['t_solve_median_ms'])} | {f(r['t_solve_iqr_ms'])} | "
                   f"{f(r['t_solve_mean_ms'])} | {k}/{r['n_runs']} | {r['contradictions_total']} | {f(r['effective_free_cells'], 0)} | "
                   f"{f(r['time_per_free_cell_median_us'], 3)} |")
    return out


def table_b(rows):
    out = ["", "## Benchmark B: coste incremental de las restricciones", ""]
    groups = defaultdict(list)
    for r in rows:
        if r["benchmark"] == "B":
            groups[(r["tileset"], int(r["total_cells"]), r["size_label"])].append(r)
    for (ts, _, size), rs in sorted(groups.items()):
        out += [f"### {ts} {size}", "",
                "| Nivel | Solver | Me (ms) | Δ Me vs nivel previo | Media (ms) | Celdas libres | Indecisas tras init | µs/celda libre (Me) | Éxito 1er intento | Contr. init/search |",
                "|---|---|---|---|---|---|---|---|---|---|"]
        prev = {}
        for r in sorted(rs, key=lambda r: (int(r["level_order"]), solver_label(r))):
            s = solver_label(r)
            me = num(r["t_solve_median_ms"])
            delta = "-"
            if s in prev and prev[s] > 0:
                delta = f"{me - prev[s]:+.2f} ms ({(me - prev[s]) / prev[s] * 100:+.0f}%)"
            prev[s] = me
            k = round(num(r["success_rate_first_attempt"]) * int(r["n_runs"]))
            out.append(f"| {r['level_id']} | {s} | {f(me)} | {delta} | {f(r['t_solve_mean_ms'])} | {f(r['effective_free_cells'], 0)} | "
                       f"{f(r['undecided_after_init_mean'], 0)} | {f(r['time_per_free_cell_median_us'], 3)} | {k}/{r['n_runs']} | "
                       f"{r['contradictions_init']}/{r['contradictions_search']} |")
        out.append("")
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    rows = load(sys.argv[1])
    bad = [r for r in rows if r.get("all_solutions_valid") != "1"]
    lines = table_a(rows) + table_b(rows)
    if bad:
        lines.insert(0, f"**ATENCIÓN: {len(bad)} configuraciones con soluciones inválidas.**\n")
    text = "\n".join(lines)
    print(text)
    if "--md" in sys.argv:
        with open(sys.argv[sys.argv.index("--md") + 1], "w", encoding="utf-8") as fh:
            fh.write(text + "\n")


if __name__ == "__main__":
    main()
