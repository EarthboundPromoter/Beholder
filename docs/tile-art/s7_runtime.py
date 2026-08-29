#!/usr/bin/env python3
"""s7_runtime.py — derive the runtime transcode table from merged_table.json.

Re-keys the review table from terrain-id to (modelPath, subImage) — the only
identity a live MapTile retains (textureBuffer.imageLayers; the terrain id is
not kept after construction, MapTile.applyTerrain) — compresses it to
sheet-default + grouped exceptions, and emits src/TileArtData.cs as a C#
constant parsed by TileArtTable at boot.

Speakability rules (owner-approved 2026-08-29):
  - class invisible / no_art        -> mute (fall through to the layer below)
  - label "(invisible)"             -> mute
  - label "shadow" / Shadows sheet  -> mute (pure lighting overlay; light is
                                       a tint, never identity — reviewer call
                                       resolved to suppression)
  - "(technical)"-tagged labels     -> dropped (technical terrains add no
                                       image layer at runtime anyway)
  - class faint                     -> speak normally

Conflicts (two terrain ids sharing a modelPath disagreeing on a (path,sub)
label) are resolved by higher usage count and REPORTED — review the report.

Re-run after any label audit fix in merged_table.json, then rebuild the mod.
"""
import json, os, sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, os.pardir, os.pardir, "src", "TileArtData.cs")

ROLE_CHAR = {"ground": "g", "wall": "w", "decoration": "d", "overlay": "o"}

def main():
    with open(os.path.join(HERE, "merged_table.json"), encoding="utf-8") as f:
        merged = json.load(f)
    with open(os.path.join(HERE, "terrain_catalog.json"), encoding="utf-8") as f:
        catalog = json.load(f)

    table = merged["table"]
    id_to_path = {tid: c.get("modelPath", "") for tid, c in catalog.items()}

    # ---- Animation-keyed rows: the water family renders via MapTile's
    # animationPath, not imageLayers (modelPath empty; TER_Ocean/Surf/Shore/
    # Shallows/Sewage). Their job labels are real ("ocean", "surf", ...);
    # the "no_art" class there means no SPRITE, not no identity. Keyed by
    # the animation name; duplicates (OceanAnimated from two ids) must agree.
    anim_rows = {}
    anim_conflicts = []
    for tid, subs in table.items():
        cat = catalog.get(tid) or {}
        anim = (cat.get("animation") or "").strip()
        if not anim or cat.get("modelPath"):
            continue
        for cell in subs.values():
            label = (cell.get("label") or "").strip()
            role = cell.get("layerRole") or "ground"
            if not label or label.startswith("("):
                continue
            prev = anim_rows.get(anim)
            if prev and prev != (label, role):
                anim_conflicts.append((anim, prev, (label, role)))
            anim_rows[anim] = (label, role)

    # ---- Re-key to (path, sub) ----
    # per (path, sub): list of (label, role, count, tid)
    by_key = defaultdict(list)
    roles_seen = set()
    dropped_technical = 0
    missing_path_ids = []
    for tid, subs in table.items():
        path = id_to_path.get(tid, "")
        if not path:
            missing_path_ids.append(tid)
            continue
        for sub, cell in subs.items():
            label = (cell.get("label") or "").strip()
            role = cell.get("layerRole") or "ground"
            cls = cell.get("class") or "visible"
            roles_seen.add(role)
            if "(technical)" in label:
                dropped_technical += 1
                continue
            mute = (
                cls in ("invisible", "no_art")
                or label == "(invisible)"
                or label == ""
                or label == "shadow"
                or path == "Shadows"
            )
            by_key[(path, int(sub))].append(
                (None if mute else label, role, cell.get("count", 0), tid)
            )

    # ---- Resolve conflicts per key ----
    resolved = {}   # (path, sub) -> (label-or-None, role)
    conflicts = []
    for key, cands in by_key.items():
        labels = {(c[0], c[1]) for c in cands}
        if len(labels) > 1:
            cands = sorted(cands, key=lambda c: -c[2])
            conflicts.append((key, cands))
        resolved[key] = (cands[0][0], cands[0][1])

    # ---- Compress per sheet: default label + grouped exceptions ----
    sheets = defaultdict(dict)     # path -> {sub: (label, role)}
    for (path, sub), lr in resolved.items():
        sheets[path][sub] = lr

    lines = []
    n_exc = 0
    for path in sorted(sheets):
        cells = sheets[path]
        # Default = the (label, role) covering the most subs; mute (None) can
        # win a sheet like Shadows outright.
        tally = defaultdict(int)
        for lr in cells.values():
            tally[lr] += 1
        default = max(tally.items(), key=lambda kv: kv[1])[0]
        def_label, def_role = default
        lines.append("S\t%s\t%s\t%s" % (
            path, def_label if def_label is not None else "-",
            ROLE_CHAR.get(def_role, "g")))
        # Exceptions grouped by (label, role), subs as ranges.
        groups = defaultdict(list)
        for sub, lr in sorted(cells.items()):
            if lr != default:
                groups[lr].append(sub)
        for (label, role), subs in sorted(
                groups.items(), key=lambda kv: (kv[0][0] or "", kv[0][1])):
            spec, start, prev = [], None, None
            for s in subs:
                if start is None:
                    start = prev = s
                elif s == prev + 1:
                    prev = s
                else:
                    spec.append(str(start) if start == prev else "%d-%d" % (start, prev))
                    start = prev = s
            if start is not None:
                spec.append(str(start) if start == prev else "%d-%d" % (start, prev))
            lines.append("G\t%s\t%s\t%s" % (
                label if label is not None else "-",
                ROLE_CHAR.get(role, "g"), ",".join(spec)))
            n_exc += len(subs)

    for anim in sorted(anim_rows):
        label, role = anim_rows[anim]
        lines.append("A\t%s\t%s\t%s" % (anim, label, ROLE_CHAR.get(role, "g")))

    tsv = "\n".join(lines)
    cs = (
        "// GENERATED by docs/tile-art/s7_runtime.py from docs/tile-art/merged_table.json\n"
        "// (tile-art classification job, 2026-08-24) - do not hand-edit; fix labels in\n"
        "// merged_table.json (the review page's source of truth) and re-run the script.\n"
        "//\n"
        "// Format: S<TAB>sheetPath<TAB>defaultLabel<TAB>role opens a sheet; G rows attach\n"
        "// grouped exceptions (label, role, sub ranges). Label \"-\" = unspeakable (mute:\n"
        "// the layer falls through to the one beneath). Roles: g round, w all,\n"
        "// d ecoration, o verlay. A<TAB>animationName<TAB>label<TAB>role rows key the\n"
        "// water family off MapTile.animationPath (no sprite sheet). Parsed by\n"
        "// TileArtTable.\n"
        "namespace SkaldAccessibility\n"
        "{\n"
        "    internal static class TileArtData\n"
        "    {\n"
        "        internal const string Tsv = @\"" + tsv.replace('"', '""') + "\";\n"
        "    }\n"
        "}\n"
    )
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(cs)

    # ---- Report ----
    print("sheets: %d   resolved pairs: %d   exception subs: %d   lines: %d"
          % (len(sheets), len(resolved), n_exc, len(lines)))
    print("roles seen:", sorted(roles_seen))
    print("dropped technical cells:", dropped_technical)
    if missing_path_ids:
        print("IDS WITH NO modelPath (skipped):", missing_path_ids)
    print("emitted:", os.path.normpath(OUT), "(%d bytes)" % len(cs.encode("utf-8")))
    print("animation rows: %d  %s" % (len(anim_rows), sorted(anim_rows.items())))
    print("animation conflicts: %d %s" % (len(anim_conflicts), anim_conflicts))
    print("conflicts: %d" % len(conflicts))
    for (path, sub), cands in conflicts[:40]:
        print("  CONFLICT %s:%d ->" % (path, sub),
              " | ".join("%r/%s c=%d [%s]" % c for c in cands))

if __name__ == "__main__":
    main()
