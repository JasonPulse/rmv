#!/usr/bin/env python3
"""Finds repeated runs of lines across the source tree.

Not a clone detector. It normalises whitespace, drops comments and braces-only
lines, then reports every normalised run of N or more lines that appears more
than once. Comments are dropped on purpose: two blocks doing the same thing with
different comments above them are still two copies of the thing.

Usage: tools/find-duplicate-blocks.py [min_lines]
"""
import hashlib
import re
import sys
from collections import defaultdict
from pathlib import Path

MIN = int(sys.argv[1]) if len(sys.argv) > 1 else 5
ROOTS = ["src", "tests"]
EXTS = {".cs", ".cshtml", ".js"}
SKIP = ("/obj/", "/bin/", "/Migrations/", "htmx.min.js")

NOISE = re.compile(r"^([{}();]|\)\s*;?|else|try|catch|break;|@\{|\}\)?;?|#nullable.*|using .*|namespace .*)$")

# A line made only of tags is page structure, not logic. Runs of </div></section>
# are the single loudest false positive and say nothing. A line with Razor or with
# text outside its tags is content and stays.
TAGS_ONLY = re.compile(r"^(</?[A-Za-z][^<>]*/?>\s*)+$")


def significant(path):
    """Lines that carry meaning, with their original line numbers."""
    out = []
    for n, raw in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        line = re.sub(r"\s+", " ", raw).strip()
        if not line or line.startswith(("//", "///", "*", "/*", "@*")):
            continue
        if NOISE.match(line) or TAGS_ONLY.match(line):
            continue
        out.append((n, line))
    return out


files = [
    p for root in ROOTS for p in Path(root).rglob("*")
    if p.suffix in EXTS and p.is_file() and not any(s in str(p) for s in SKIP)
]

runs = defaultdict(list)
for path in files:
    lines = significant(path)
    for i in range(len(lines) - MIN + 1):
        window = [t for _, t in lines[i:i + MIN]]
        key = hashlib.sha1("\n".join(window).encode()).hexdigest()
        runs[key].append((path, lines[i][0], window))

dupes = {k: v for k, v in runs.items() if len({(p, n) for p, n, _ in v}) > 1}

# Keep only the longest run at each start, so one duplicated 20-line block is one
# finding rather than sixteen overlapping ones.
seen = set()
reported = 0
for key, hits in sorted(dupes.items(), key=lambda kv: -len(kv[1])):
    where = sorted((str(p), n) for p, n, _ in hits)
    if any((p, n) in seen for p, n in where):
        continue
    for spot in where:
        seen.add(spot)
    reported += 1
    print(f"\n=== {len(where)} copies of {MIN} lines ===")
    for p, n in where:
        print(f"  {p}:{n}")
    for line in hits[0][2]:
        print(f"    | {line[:100]}")

print(f"\n{reported} duplicated block(s) at >= {MIN} significant lines across {len(files)} files.")
sys.exit(1 if reported else 0)
