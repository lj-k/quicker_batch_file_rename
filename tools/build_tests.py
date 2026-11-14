#!/usr/bin/env python3
"""Generate a compilable C# test file from the Quicker core snippet.

Quicker's auxiliary C# editor accepts using directives plus static members,
but not a namespace/class wrapper. For local tests we wrap the same body in a
static class so the exact production code is compiled and executed.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "RenamerCore.cs"
HARNESS = ROOT / "tests" / "TestHarness.cs"
OUT = ROOT / "tests" / "GeneratedTests.cs"

lines = CORE.read_text(encoding="utf-8").splitlines()
usings = []
body = []
in_header = True
for line in lines:
    stripped = line.strip()
    if in_header and (stripped.startswith("using ") or stripped == ""):
        if stripped.startswith("using "):
            usings.append(line)
        continue
    in_header = False
    body.append(line)

harness = HARNESS.read_text(encoding="utf-8")
harness_lines = []
for line in harness.splitlines():
    if line.strip().startswith("using "):
        continue
    harness_lines.append(line)
harness = "\n".join(harness_lines)
generated = "\n".join(usings) + "\n\npublic static class CoreUnderTest\n{\n"
generated += "\n".join(body)
generated += "\n}\n\n"
generated += harness
OUT.write_text(generated, encoding="utf-8")
print("generated", OUT, len(generated), "chars")
