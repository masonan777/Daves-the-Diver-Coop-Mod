#!/usr/bin/env python3
"""Annotate Cpp2IL ISIL dumps with method names.

Usage: annotate_isil.py funcs_all.txt OUT_DIR file1.txt [file2.txt ...]
funcs_all.txt lines: "<RVA hex> <Full.Method.Name>" (image base 0x180000000).
Writes OUT_DIR/<name>.txt with only the ISIL section of each method (raw
disassembly dropped) and each call/jump target replaced by a method name.
"""
import os
import re
import sys

BASE = 0x180000000


def load_map(path):
    names = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            parts = line.rstrip("\n").split(" ", 1)
            if len(parts) != 2:
                continue
            try:
                va = int(parts[0], 16) + BASE
            except ValueError:
                continue
            names.setdefault(va, []).append(parts[1])
    return names


ADDR = re.compile(r"0x(18[0-9A-Fa-f]{7})\b")


def label(names, va):
    n = names.get(va)
    if not n:
        return None
    if len(n) == 1:
        return n[0]
    return n[0] + f" (+{len(n) - 1} shared)"


def annotate(names, src, dst):
    out = []
    mode = None
    with open(src, encoding="utf-8", errors="replace") as f:
        for line in f:
            s = line.rstrip("\n")
            if s.startswith("Type:") or s.startswith("Method:"):
                out.append("" if s.startswith("Method:") else "")
                out.append(s)
                mode = None
                continue
            if s.strip() == "Disassembly:":
                mode = "dis"
                continue
            if s.strip() == "ISIL:":
                mode = "isil"
                continue
            if mode != "isil" or not s.strip():
                continue

            def rep(m):
                va = int(m.group(1), 16)
                lab = label(names, va)
                return f"<{lab}>" if lab else m.group(0)

            out.append(ADDR.sub(rep, s))
    with open(dst, "w", encoding="utf-8") as f:
        f.write("\n".join(out) + "\n")


def main():
    names = load_map(sys.argv[1])
    os.makedirs(sys.argv[2], exist_ok=True)
    for src in sys.argv[3:]:
        annotate(names, src, os.path.join(sys.argv[2], os.path.basename(src)))
    print(f"{len(names)} addresses; annotated {len(sys.argv) - 3} files")


if __name__ == "__main__":
    main()
