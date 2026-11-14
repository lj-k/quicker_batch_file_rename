#!/usr/bin/env python3
"""Build the two delivery variants from the shared XAML/Aux sources."""
import re
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC_XAML = ROOT / "src" / "CustomWindow.xaml"
DIST_XAML = ROOT / "dist" / "CustomWindow.xaml"
DIST_AUX = ROOT / "dist" / "QuickerAux.cs"
RELEASE = ROOT / "release"

SCHEME1 = RELEASE / "方案1-仅重命名不执行"
SCHEME2 = RELEASE / "方案2-执行不输出列表"
SCHEME3 = RELEASE / "方案3-重复获取+重命名+执行"


def remove_marked_block(text, begin, end):
    pattern = re.compile(
        r"[ \t]*<!-- " + re.escape(begin) + r" -->.*?<!-- " + re.escape(end) + r" -->[ \t]*\n?",
        re.S,
    )
    return pattern.sub("", text)


def strip_markers(text, begin, end):
    text = re.sub(r"[ \t]*<!-- " + re.escape(begin) + r" -->[ \t]*\n?", "", text)
    text = re.sub(r"[ \t]*<!-- " + re.escape(end) + r" -->[ \t]*\n?", "", text)
    return text


def write_variant(name, xaml_text, mode):
    folder = SCHEME1 if mode == 1 else (SCHEME2 if mode == 2 else SCHEME3)
    folder.mkdir(parents=True, exist_ok=True)
    xaml_path = folder / "CustomWindow.xaml"
    xaml_path.write_text(xaml_text, encoding="utf-8")
    ET.parse(xaml_path)

    aux_text = DIST_AUX.read_text(encoding="utf-8")
    replaced = aux_text.replace(
        "private static readonly int DeliveryMode = 0;",
        "private static readonly int DeliveryMode = " + str(mode) + ";",
    )
    if replaced == aux_text:
        raise SystemExit("DeliveryMode constant not found in deployed Aux code")
    (folder / "QuickerAux.cs").write_text(replaced, encoding="utf-8")
    print("built", name, "->", folder)


def main():
    source = SRC_XAML.read_text(encoding="utf-8")

    full = strip_markers(source, "RELEASE-EXEC-BEGIN", "RELEASE-EXEC-END")
    full = strip_markers(full, "RELEASE-APPLY-BEGIN", "RELEASE-APPLY-END")
    full = strip_markers(full, "RELEASE-FINISH-BEGIN", "RELEASE-FINISH-END")
    DIST_XAML.parent.mkdir(parents=True, exist_ok=True)
    DIST_XAML.write_text(full, encoding="utf-8")
    ET.parse(DIST_XAML)

    scheme1 = remove_marked_block(source, "RELEASE-EXEC-BEGIN", "RELEASE-EXEC-END")
    scheme1 = remove_marked_block(scheme1, "RELEASE-FINISH-BEGIN", "RELEASE-FINISH-END")
    scheme1 = strip_markers(scheme1, "RELEASE-APPLY-BEGIN", "RELEASE-APPLY-END")
    scheme1 = scheme1.replace('x:Name="BtnApply" Content="生成输出"', 'x:Name="BtnApply" Content="执行"')
    write_variant("scheme1", scheme1, 1)

    scheme2 = remove_marked_block(source, "RELEASE-APPLY-BEGIN", "RELEASE-APPLY-END")
    scheme2 = strip_markers(scheme2, "RELEASE-EXEC-BEGIN", "RELEASE-EXEC-END")
    scheme2 = strip_markers(scheme2, "RELEASE-FINISH-BEGIN", "RELEASE-FINISH-END")
    write_variant("scheme2", scheme2, 2)

    # 方案3与方案2共用“执行/完成”界面，额外通过重新获取按钮支持多批处理。
    scheme3 = remove_marked_block(source, "RELEASE-APPLY-BEGIN", "RELEASE-APPLY-END")
    scheme3 = strip_markers(scheme3, "RELEASE-EXEC-BEGIN", "RELEASE-EXEC-END")
    scheme3 = strip_markers(scheme3, "RELEASE-FINISH-BEGIN", "RELEASE-FINISH-END")
    scheme3 = scheme3.replace('Title="批量重命名器"',
                              'Title="批量重命名器（重复获取+重命名+执行）"')
    scheme3 = scheme3.replace('Text="左侧配置，右侧点击胶囊插入模板"',
                              'Text="重新获取选择的文件，可连续处理多批"')
    write_variant("scheme3", scheme3, 3)


if __name__ == "__main__":
    main()
