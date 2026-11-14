#!/usr/bin/env python3
"""Build the deployed Quicker auxiliary code and a compilable test wrapper."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "RenamerCore.cs"
AUX = ROOT / "src" / "CustomWindow.Aux.cs"
DIST = ROOT / "dist" / "QuickerAux.cs"
WRAPPED = ROOT / "tests" / "GeneratedAux.cs"


def split_usings(path):
    usings = []
    body = []
    in_header = True
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if in_header and (stripped.startswith("using ") or stripped == ""):
            if stripped.startswith("using "):
                usings.append(line)
            continue
        in_header = False
        body.append(line)
    return usings, body


core_usings, core_body = split_usings(CORE)
aux_usings, aux_body = split_usings(AUX)

seen = set()
usings = []
for line in core_usings + aux_usings:
    if line not in seen:
        seen.add(line)
        usings.append(line)

header = "// 批量重命名器 - Quicker 辅助C#代码（由 tools/build_quicker_code.py 生成）\n// 版本：0.06.7\n"
deployed = header + "\n".join(usings) + "\n\n" + "\n".join(core_body) + "\n\n" + "\n".join(aux_body) + "\n"
DIST.parent.mkdir(parents=True, exist_ok=True)
DIST.write_text(deployed, encoding="utf-8")

wrapped = (
    header
    + "\n".join(usings)
    + "\n\npublic static class QuickerAuxUnderTest\n{\n"
    + "\n".join(core_body)
    + "\n\n"
    + "\n".join(aux_body)
    + "\n}\n"
)
WRAPPED.write_text(wrapped, encoding="utf-8")

print("deployed:", DIST, len(deployed), "chars")
print("wrapped :", WRAPPED, len(wrapped), "chars")
