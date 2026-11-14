#!/usr/bin/env python3
"""Delivery checks for XAML, generated Quicker code, documents and versions."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
errors = []
notes = []


def check(condition, message):
    if condition:
        notes.append("OK   " + message)
    else:
        errors.append("FAIL " + message)


xaml_path = ROOT / "src" / "CustomWindow.xaml"
xaml_text = xaml_path.read_text(encoding="utf-8")
tree = ET.parse(xaml_path)
root = tree.getroot()
check(root.tag.endswith("Window"), "XAML root is Window")
check('xmlns:qk="https://getquicker.net"' in xaml_text, "XAML registers qk namespace")
check("x:Class" not in xaml_text, "XAML has no x:Class")

xaml_names = set()
for element in root.iter():
    for key, value in element.attrib.items():
        if key.endswith("}Name") or key == "Name":
            xaml_names.add(value)
    for key in element.attrib:
        local = key.split("}")[-1]
        if local in {
            "Click", "SelectionChanged", "TextChanged", "LostFocus",
            "Loaded", "Closed", "PreviewKeyDown", "MouseDown", "MouseUp",
        }:
            errors.append("FAIL XAML declares event handler attribute: " + local)

aux_text = (ROOT / "src" / "CustomWindow.Aux.cs").read_text(encoding="utf-8")
code_names = set(re.findall(r'(?:FindName|AttachClick)\("([^"]+)"\)', aux_text))
missing = sorted(code_names - xaml_names)
check(not missing, "all code-referenced names exist in XAML" + ("" if not missing else ": " + ", ".join(missing)))

required = {
    "RootModeCombo", "RootPathText", "BrowseRootButton", "StartYearCombo", "DateSourceCombo",
    "RealTimeCheck", "BtnPreview", "FallbackEnabledCheck",
    "Fallback1Combo", "Fallback2Combo", "Fallback3Combo", "Fallback4Combo", "Fallback5Text",
    "MissingModeCombo", "MissingText", "ErrorBanner", "ErrorBannerText",
    "PresetPopup", "PresetListBox",
    "AlgorithmCombo", "DigitsText", "PrefixText", "SuffixText", "ChecksumBadge",
    "TemplateEditor", "PreviewGrid", "StatusText", "Progress", "BtnCancel", "BtnApply",
    "BtnLoadPreset", "BtnSavePreset", "BtnResetTemplate",
    "BtnTokenYyyy", "BtnTokenMm", "BtnTokenDd", "BtnTokenRoot",
    "BtnTokenName", "BtnTokenExt", "BtnTokenHash",
}
check(required <= xaml_names, "all required controls are named in XAML")

dist_text = (ROOT / "dist" / "QuickerAux.cs").read_text(encoding="utf-8")
dist_code = "\n".join(
    line for line in dist_text.splitlines() if not line.lstrip().startswith("//")
)
check("public static void OnWindowCreated" in dist_code, "deployed code has OnWindowCreated")
check("public static void OnWindowLoaded" in dist_code, "deployed code has OnWindowLoaded")
check("BuildChecksumBadge" in dist_code, "deployed code contains checksum core")
check("ComputeBlake3Hex" in dist_code, "deployed code contains BLAKE3")
check("namespace " not in dist_code, "deployed code has no namespace declaration")
check("public class " not in dist_code, "deployed code has no class declaration")
check("public static class " not in dist_code, "deployed code has no static class declaration")
check("// 版本：0.03" in dist_text, "deployed code version is 0.03")

core_text = (ROOT / "src" / "RenamerCore.cs").read_text(encoding="utf-8")
check("// 版本：0.03" in core_text, "core source version is 0.03")
check('preset["presetVersion"] = "0.03"' in core_text, "preset version is 0.03")
check('// 版本：0.03' in aux_text, "aux source version is 0.03")
check("XAML 版本：0.03" in xaml_text, "XAML version is 0.03")

# Using directives must be at the top of the deployment snippet.
first_non_using = None
for index, line in enumerate(dist_text.splitlines()):
    stripped = line.strip()
    if stripped.startswith("//") or stripped == "":
        continue
    if not stripped.startswith("using "):
        first_non_using = index
        break
check(first_non_using is not None, "deployed code has a code body")
if first_non_using is not None:
    bad_using = [
        line.strip()
        for line in dist_text.splitlines()[first_non_using:]
        if line.strip().startswith("using ")
        and line.strip().endswith(";")
        and "(" not in line
    ]
    check(not bad_using, "no using directives after the code body")

req_text = (ROOT / "doc" / "需求文档.md").read_text(encoding="utf-8")
check(req_text.count("0.0.4") == 1 and "> 版本：0.0.4" in req_text, "requirements doc version 0.0.4 is unique")
check("2020.06/aaa/bbb" in req_text, "requirements example includes the 2020.06 level")

for name, version in [
    ("doc/开发说明.md", "0.03"),
    ("doc/测试报告.md", "0.03"),
]:
    path = ROOT / name
    if path.exists():
        text = path.read_text(encoding="utf-8")
        check(text.count(version) >= 1 and ("> 版本：" + version) in text, name + " has version " + version)
    else:
        errors.append("FAIL missing " + name)

changelog = ROOT / "CHANGELOG.md"
if changelog.exists():
    changelog_text = changelog.read_text(encoding="utf-8")
    check("[0.03]" in changelog_text, "CHANGELOG has 0.03 entry")
else:
    errors.append("FAIL missing CHANGELOG.md")

agents = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
check("版本：0.01" in agents, "AGENTS.md has version 0.01")
check("所有的用户输入完整记录在userinput文件夹" in agents, "AGENTS.md keeps user rule")

inputs = sorted((ROOT / "userinput").glob("*.md"))
check(len(inputs) >= 5, "userinput contains at least 5 records")

for note in notes:
    print(note)
print()
if errors:
    for error in errors:
        print(error)
    print("RESULT FAILED:", len(errors))
    raise SystemExit(1)
print("RESULT OK:", len(notes), "checks passed")
