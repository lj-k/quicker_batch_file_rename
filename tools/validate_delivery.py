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
    "PreferExifCheck", "RealTimeCheck", "BtnPreview",
    "FilterCardTitle", "FilterExtensionsText", "FilterModeCombo", "BtnLoadExtPreset", "BtnSaveExtPreset",
    "Fallback1Combo", "Fallback2Combo", "Fallback3Combo", "Fallback4Combo", "Fallback5Combo",
    "FallbackCustomText", "ErrorBanner", "ErrorBannerText",
    "PresetPopup", "PresetListBox",
    "AlgorithmCombo", "DigitsText", "PrefixText", "SuffixText", "ChecksumBadge",
    "TemplateEditor", "PreviewGrid", "StatusText", "PerfText", "Progress", "BtnCancel", "BtnApply",
    "GridFilterText", "GridStatusFilterCombo", "GridFilterCountText",
    "BtnLoadPreset", "BtnSavePreset", "BtnResetTemplate",
    "BtnTokenYyyy", "BtnTokenMm", "BtnTokenDd", "BtnTokenRoot",
    "BtnTokenName", "BtnTokenExt", "BtnTokenHash",
    "ConfigPanel", "ExecParallelCombo", "ExecVerifyCheck", "BtnExecute",
    "ExecProgress", "ExecStatusText", "BtnFinish",
}
check(required <= xaml_names, "all required controls are named in XAML")
check('Header="动作"' in xaml_text, "DataGrid has action column")
check('CanUserSortColumns="True"' in xaml_text, "DataGrid columns are sortable")
check('Header="异常状态"' in xaml_text and 'Header="执行结果"' in xaml_text,
      "DataGrid has error and execution result columns")
check('Header="源路径名"' in xaml_text and 'SortMemberPath="SourcePath"' in xaml_text,
      "source path column is sortable")
check('Header="目标路径名/文件名"' in xaml_text and 'SortMemberPath="TargetPath"' in xaml_text,
      "target path column is sortable")
check('Binding="{Binding [ExecState]}" Value="done"' in xaml_text,
      "DataGrid has execution state background trigger")
check('Property="Foreground" Value="#027A48"' not in xaml_text,
      "execution state does not change font color")

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
check(dist_text.splitlines()[1] == "// 版本：0.06.12", "deployed code version is 0.06.12")
check("extensionPresets" in dist_text, "deployed code maps extension preset variable")
check("SetVarValue" in dist_text, "deployed code attempts realtime variable write")
check("GetVarValue" in dist_text, "deployed code attempts startup variable read")
check("PrimePresetVariables" in dist_text, "deployed code primes preset variables")
check("NormalizePresetVariable" in dist_text, "deployed code normalizes JSON preset state")
check("TryParseJson" in dist_text, "deployed code parses JSON preset state")
check("FindVariableMethod" in dist_text, "deployed code resolves interface variable methods")
check("preferExifOnFilenameConflict" in dist_text, "deployed code contains exif conflict option")
check("PreferExifCheck_Changed" in dist_text, "deployed code handles exif conflict toggle")
check("BuildFlatPresetDictionary" in dist_text, "deployed code builds flat preset dictionary")
check("ParsePresetDictionary" in dist_text, "deployed code parses flat preset dictionary")
check("PresetConfigKeys" in dist_text, "deployed code uses unified preset config keys")
check("BuildFlatExtensionPresetDictionary" in dist_text,
      "deployed code builds flat extension preset dictionary")
check("ParseExtensionPresetDictionary" in dist_text,
      "deployed code parses flat extension preset dictionary")
check("IsVariableMissing" in dist_text, "deployed code falls back when mapped preset is empty")
check("ExecuteOneFile" in dist_text, "deployed code contains execution worker")
check("CopyFileAdaptive" in dist_text, "deployed code contains adaptive transfer engine")
check("DeliveryMode" in dist_text, "deployed code contains delivery mode")
check("Finish_Click" in dist_text, "deployed code contains finish handler")
check('"actions"' in dist_text, "deployed code outputs action list")
check("FlushExecutionUi" in dist_text, "deployed code batches execution row refresh")
check("VerifyMovedFileName" in dist_text, "deployed code skips content hash for same-volume move")
check("EnsureExecutionDirectory" in dist_text, "deployed code caches execution directories")
check("TemplateNeedsHash" in dist_text, "deployed code caches template checksum detection")
check("BitmapCacheOption.None" in dist_text, "deployed code reads EXIF metadata only")

core_text = (ROOT / "src" / "RenamerCore.cs").read_text(encoding="utf-8")
check("// 版本：0.06.12" in core_text, "core source version is 0.06.12")
check('preset["presetVersion"] = "0.05"' in core_text, "preset schema version is 0.05")
check("preferExifOnFilenameConflict" in core_text, "core contains exif conflict config")
check("BuildFlatPresetDictionary" in core_text, "core contains flat preset builder")
check("ParsePresetDictionary" in core_text, "core contains flat preset parser")
check("PresetConfigKeys" in core_text, "core contains unified preset keys")
check("BuildFlatExtensionPresetDictionary" in core_text,
      "core contains flat extension preset builder")
check("ParseExtensionPresetDictionary" in core_text,
      "core contains flat extension preset parser")
check("ShouldSkipByFilter" in core_text, "core contains filter decision")
check("ResolveAction" in core_text, "core contains action resolution")
check("CopyFileWithRetry" in core_text, "core contains copy retry engine")
check("SelectCopyBufferSizeForDrive" in core_text, "core contains drive buffer profile")
check("ClampChecksumDigits" in core_text, "core contains unified checksum digits clamp")
check("GetVolumeRoot" in core_text, "core contains deterministic volume root")
check("MaxJsonDepth" in core_text, "core contains JSON depth guard")
check("ExecutionCancelCheck" in aux_text, "aux cancels in-flight transfers")
check("IsCloudPlaceholderAttributes" in core_text, "core detects cloud placeholder attributes")
check("TryCompareFileContent" in core_text, "core compares conflicting file content")
check("BuildNumberedSuffixPath" in core_text, "core builds numbered suffix paths")
check("PreviewGrid_Sorting" in aux_text, "aux wires preview sorting")
check("GridRowFilter" in aux_text, "aux wires preview filtering")
check("删除原文件" in aux_text, "aux executes delete-source dedupe action")
check("RunIndexedParallel" in aux_text, "aux has indexed parallel scheduler")
check("ScanChunkSize" in aux_text, "aux scans attributes in parallel chunks")
check("RecomputeAll(bool synchronous)" in aux_text, "aux supports synchronous layout recompute")
check("BuildPerfLine" in aux_text, "aux reports scan and layout performance")
check("ClassifyPerformanceBottleneck" in core_text, "core classifies CPU/IO bottleneck")
check("UpdatePerfText" in aux_text and "PerfText" in xaml_text,
      "aux wires performance text into the footer")
check('// 版本：0.06.12' in aux_text, "aux source version is 0.06.12")
check("XAML 版本：0.06.12" in xaml_text, "XAML version is 0.06.12")

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
check("> 版本：0.0.20" in req_text and "> 版本：0.0.10" not in req_text,
      "requirements doc header version is 0.0.20")
check("2020.06/aaa/bbb" in req_text, "requirements example includes the 2020.06 level")
check("文件传输性能与RAS" in req_text, "requirements includes transfer performance and RAS")
check("双交付方案与完成按钮" in req_text, "requirements includes dual delivery and finish button")
check("预设变量未持久化排查" in req_text, "requirements includes preset persistence")
check("预设变量直接赋值" in req_text, "requirements includes direct preset assignment")
check("winContext.Context.SetVarValue" in req_text, "requirements includes direct SetVarValue")
check("交互窗口与执行性能优化" in req_text, "requirements includes performance optimization")

for name, version in [
    ("doc/开发说明.md", "0.06.12"),
    ("doc/测试报告.md", "0.06.12"),
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
    check("[0.06.12]" in changelog_text, "CHANGELOG has 0.06.12 entry")
else:
    errors.append("FAIL missing CHANGELOG.md")

test_report_text = (ROOT / "doc" / "测试报告.md").read_text(encoding="utf-8")
check("六团队性能会诊记录" in test_report_text, "test report has six-team performance review")
check("411 项通过" in test_report_text, "test report records 411 passing tests")
check("第一性原理评审" in test_report_text, "test report has first-principles review record")

agents = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
check("版本：0.02" in agents, "AGENTS.md has version 0.02")
check("所有的用户输入完整记录在userinput文件夹" in agents, "AGENTS.md keeps user rule")
check("共享代码，一同开发、更新和交付" in agents,
      "AGENTS.md keeps shared delivery rule")

inputs = sorted((ROOT / "userinput").glob("*.md"))
check(len(inputs) >= 11, "userinput contains at least 11 records")

release = ROOT / "release"
s1 = release / "方案1-仅重命名不执行"
s2 = release / "方案2-执行不输出列表"
for folder in [s1, s2]:
    check((folder / "CustomWindow.xaml").exists(), str(folder.name) + " has XAML")
    check((folder / "QuickerAux.cs").exists(), str(folder.name) + " has Aux")
    check((folder / "QuickerAux.cs").read_text(encoding="utf-8").splitlines()[1] == "// 版本：0.06.12",
          str(folder.name) + " Aux version 0.06.12")

s1_xaml = (s1 / "CustomWindow.xaml").read_text(encoding="utf-8")
s2_xaml = (s2 / "CustomWindow.xaml").read_text(encoding="utf-8")
check("BtnExecute" not in s1_xaml, "scheme1 has no execute button")
check("ExecParallelCombo" not in s1_xaml, "scheme1 has no execution config")
check("BtnApply" in s1_xaml and "BtnFinish" not in s1_xaml, "scheme1 has apply and no finish")
check('x:Name="BtnApply" Content="执行"' in s1_xaml, "scheme1 apply button is labeled execute")
check("BtnApply" not in s2_xaml, "scheme2 has no apply button")
check("BtnExecute" in s2_xaml and "BtnFinish" in s2_xaml, "scheme2 has execute and finish")
check("private static readonly int DeliveryMode = 1;" in (s1 / "QuickerAux.cs").read_text(encoding="utf-8"),
      "scheme1 delivery mode 1")
check("private static readonly int DeliveryMode = 2;" in (s2 / "QuickerAux.cs").read_text(encoding="utf-8"),
      "scheme2 delivery mode 2")
check("RELEASE-EXEC" not in s1_xaml and "RELEASE-APPLY" not in s1_xaml,
      "scheme1 xaml markers removed")
check("RELEASE-EXEC" not in s2_xaml and "RELEASE-APPLY" not in s2_xaml,
      "scheme2 xaml markers removed")

for note in notes:
    print(note)
print()
if errors:
    for error in errors:
        print(error)
    print("RESULT FAILED:", len(errors))
    raise SystemExit(1)
print("RESULT OK:", len(notes), "checks passed")
