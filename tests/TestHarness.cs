using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class TestHarness
{
    private static int _passed = 0;
    private static int _failed = 0;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine("PASS  " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL  " + name);
        }
    }

    private static void CheckEqual(string expected, string actual, string name)
    {
        bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
        if (!ok)
            Console.WriteLine("      expected: " + expected + "\n      actual  : " + actual);
        Check(ok, name);
    }

    private static void CheckEqual(int expected, int actual, string name)
    {
        bool ok = expected == actual;
        if (!ok)
            Console.WriteLine("      expected: " + expected + "\n      actual  : " + actual);
        Check(ok, name);
    }

    private static byte[] MakePattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    private static void TestStandardHashes()
    {
        string error;
        byte[] abc = Encoding.ASCII.GetBytes("abc");

        CheckEqual("900150983cd24fb0d6963f7d28e17f72",
            CoreUnderTest.ComputeHashHexFromBytes(abc, "MD5", out error), "MD5(abc)");
        CheckEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            CoreUnderTest.ComputeHashHexFromBytes(abc, "SHA-256", out error), "SHA-256(abc)");
        CheckEqual("352441c2",
            CoreUnderTest.ComputeHashHexFromBytes(abc, "CRC32", out error), "CRC32(abc)");
        CheckEqual("cbf43926",
            CoreUnderTest.ComputeHashHexFromBytes(Encoding.ASCII.GetBytes("123456789"), "CRC32", out error),
            "CRC32(123456789)");
        CheckEqual("00000000",
            CoreUnderTest.ComputeHashHexFromBytes(new byte[0], "CRC32", out error), "CRC32(empty)");
    }

    private static void TestBlake3()
    {
        string[] lines = File.ReadAllLines("tests/blake3_vectors.txt", Encoding.UTF8);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            string[] parts = line.Split('|');
            int length = int.Parse(parts[0], CultureInfo.InvariantCulture);
            string expected = parts[1];
            string error;
            string actual = CoreUnderTest.ComputeHashHexFromBytes(MakePattern(length), "BLAKE3", out error);
            CheckEqual(expected, actual, "BLAKE3 official vector len=" + length);
        }
    }

    // 文件流哈希：覆盖读缓冲区与 BLAKE3 分块边界。
    private static void TestHashStreamBoundaries()
    {
        string path = Path.Combine(Path.GetTempPath(), "qk-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        int[] sizes = new int[] { 0, 1, 63, 64, 65, 1023, 1024, 1025, 81919, 81920, 81921 };
        string[] algorithms = new string[] { "CRC32", "MD5", "SHA-256", "BLAKE3" };
        try
        {
            for (int s = 0; s < sizes.Length; s++)
            {
                byte[] data = MakePattern(sizes[s]);
                File.WriteAllBytes(path, data);
                for (int a = 0; a < algorithms.Length; a++)
                {
                    string error;
                    string fromBytes = CoreUnderTest.ComputeHashHexFromBytes(data, algorithms[a], out error);
                    string fromStream = CoreUnderTest.ComputeHashHex(path, algorithms[a], out error);
                    CheckEqual(fromBytes, fromStream,
                        "hash stream: " + algorithms[a] + " len=" + sizes[s]);
                }
            }
        }
        finally
        {
            try { File.Delete(path); }
            catch { }
        }
    }

    private static void TestFilenameDate()
    {
        DateTime date;
        int year = DateTime.Now.Year;

        Check(CoreUnderTest.TryExtractFilenameDate("IMG_20200202_123.jpg", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: compact");
        Check(CoreUnderTest.TryExtractFilenameDate("2020-02-02.jpg", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: dashed");
        Check(CoreUnderTest.TryExtractFilenameDate("x2020-02-02y.jpg", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: letters around");
        Check(!CoreUnderTest.TryExtractFilenameDate("12020-02-02.jpg", 2000, out date),
            "filename date: reject leading digit");
        Check(CoreUnderTest.TryExtractFilenameDate("2020-02-021.jpg", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: allow trailing digit after dashed date");
        Check(CoreUnderTest.TryExtractFilenameDate("202002021.jpg", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: allow trailing digit after compact date");
        Check(!CoreUnderTest.TryExtractFilenameDate("1999-12-31.jpg", 2000, out date),
            "filename date: reject before start year");
        Check(!CoreUnderTest.TryExtractFilenameDate("2024-02-30.jpg", 2000, out date),
            "filename date: reject invalid day");
        Check(CoreUnderTest.TryExtractFilenameDate("20200202_2021-05-05.jpg", 2000, out date)
            && date == new DateTime(2021, 5, 5), "filename date: dashed format priority");
        Check(CoreUnderTest.TryExtractFilenameDate("abc20200202def", 2000, out date)
            && date == new DateTime(2020, 2, 2), "filename date: compact inside letters");
        Check(!CoreUnderTest.TryExtractFilenameDate("2020-2-02.jpg", 2000, out date),
            "filename date: reject non-padded");
        Check(!CoreUnderTest.TryExtractFilenameDate("D.TXT", 2000, out date),
            "filename date: no date");
        Check(CoreUnderTest.TryExtractFilenameDate(year.ToString(CultureInfo.InvariantCulture) + "-01-02.jpg",
            year - 1, out date) && date.Year == year, "filename date: current year boundary");
        Check(!CoreUnderTest.TryExtractFilenameDate((year + 1).ToString(CultureInfo.InvariantCulture) + "-01-02.jpg",
            2000, out date), "filename date: reject future year");
    }

    private static void TestChecksumBadge()
    {
        CheckEqual("[CRC32-7f7f7f7f]",
            CoreUnderTest.BuildChecksumBadge("CRC32", 8, "[", "]", "7f7f7f7f"), "badge: CRC32 full");
        CheckEqual("[CRC32_4-7f7f]",
            CoreUnderTest.BuildChecksumBadge("CRC32", 4, "[", "]", "7f7f7f7f"), "badge: CRC32 truncated");
        CheckEqual("CHK:CRC32_4-7f7f:END",
            CoreUnderTest.BuildChecksumBadge("CRC32", 4, "CHK:", ":END", "7f7f7f7f"), "badge: prefix/suffix");
        CheckEqual("[SHA-256-" + new string('a', 64) + "]",
            CoreUnderTest.BuildChecksumBadge("SHA256", 64, "[", "]", new string('a', 64)), "badge: SHA-256");
        CheckEqual("[MD5-" + new string('b', 32) + "]",
            CoreUnderTest.BuildChecksumBadge("md5", 32, "[", "]", new string('b', 32)), "badge: MD5 lowercase input");
        CheckEqual("[BLAKE3-" + new string('c', 64) + "]",
            CoreUnderTest.BuildChecksumBadge("BLAKE3", 64, "[", "]", new string('c', 64)), "badge: BLAKE3");
        CheckEqual("7f7f", CoreUnderTest.TruncateHash("7f7f7f7f", 4), "truncate: head");
        CheckEqual("[CRC32-…]",
            CoreUnderTest.BuildPendingChecksumBadge("CRC32", 8, "[", "]"), "badge: pending");
        CheckEqual("CRC32",
            CoreUnderTest.GetAlgorithmLabel("CRC32", 8), "badge: full length label omits digits");
        CheckEqual("CRC32_4",
            CoreUnderTest.GetAlgorithmLabel("CRC32", 4), "badge: truncated label includes digits");
    }

    private static void TestTemplateAndPath()
    {
        string target;
        string error;
        string template = "【根路径】/【日期yyyy】.【日期mm】/aaa/bbb/【源文件名】-【校验码】.【扩展名】";
        bool ok = CoreUnderTest.TryBuildTargetPath(template, @"C:\A\B\C",
            new DateTime(2020, 6, 15), null, "D", "TXT", "[CRC32_4-7f7f]", out target, out error);
        Check(ok, "path: example builds");
        CheckEqual(@"C:\A\B\C\2020.06\aaa\bbb\D-[CRC32_4-7f7f].TXT", target, "path: example result");
        if (!ok) Console.WriteLine("      error: " + error);

        ok = CoreUnderTest.TryBuildTargetPath("【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: relative template gets root");

        ok = CoreUnderTest.TryBuildTargetPath("D:/Out/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "TXT", "", out target, out error);
        Check(ok && target == @"D:\Out\D.TXT", "path: absolute template ignores root");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.txt", @"C:\A",
            new DateTime(2020, 6, 15), null, "【日期yyyy】", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\【日期yyyy】.txt", "path: token in value is not recursively replaced");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【未知】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "TXT", "", out target, out error);
        Check(!ok && error != null && error.IndexOf("未知胶囊") >= 0, "path: unknown token rejected");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/../D.TXT", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "TXT", "", out target, out error);
        Check(!ok, "path: parent segment rejected");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "", "", out target, out error);
        Check(!ok, "path: empty extension leaves trailing dot and fails");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), null, "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: simple template");

        Check(CoreUnderTest.DefaultTemplate.IndexOf("aaa/bbb", StringComparison.Ordinal) >= 0,
            "path: default template contains aaa/bbb");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【日期yyyy】/【源文件名】.【扩展名】", @"C:\A",
            null, "0000", "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\0000\D.TXT", "path: missing date uses replacement text");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\A",
            null, "", "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: missing date empty text is allowed");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【日期yyyy】【日期mm】【日期dd】/【源文件名】.【扩展名】", @"C:\A",
            null, "", "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: all empty date tokens collapse to empty");

        string normalized = CoreUnderTest.NormalizeTargetPath("C://A/B//C", "", out error);
        CheckEqual(@"C:\A\B\C", normalized, "path: collapse duplicate separators");
        normalized = CoreUnderTest.NormalizeTargetPath(@"\\server\share\\folder", "", out error);
        CheckEqual(@"\\server\share\folder", normalized, "path: preserve UNC prefix");
        normalized = CoreUnderTest.NormalizeTargetPath("B/C.TXT", @"C:\A", out error);
        CheckEqual(@"C:\A\B\C.TXT", normalized, "path: combine root");

        Check(CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B.TXT", out error), "validate: normal drive path");
        Check(CoreUnderTest.ValidateCrossPlatformPath(@"\\server\share\B.TXT", out error), "validate: UNC path");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"\\server\share", out error), "validate: UNC without file");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\..\B.TXT", out error), "validate: dot-dot");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B*.TXT", out error), "validate: wildcard");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:A\B.TXT", out error), "validate: drive-relative");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B.", out error), "validate: trailing dot");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B ", out error), "validate: trailing space");
        Check(CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B", out error), "validate: path without extension is valid");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B<C.TXT", out error)
            && error.IndexOf("<") >= 0, "validate: cross-platform invalid char <");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B?C.TXT", out error)
            && error.IndexOf("?") >= 0, "validate: cross-platform invalid char ?");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B:C.TXT", out error)
            && error.IndexOf(":") >= 0, "validate: cross-platform invalid char :");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\CON.txt", out error)
            && error.IndexOf("CON") >= 0, "validate: Windows reserved file name");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\LPT1\B.txt", out error)
            && error.IndexOf("LPT1") >= 0, "validate: Windows reserved directory name");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\" + new string('a', 260), out error)
            && error.IndexOf("255") >= 0, "validate: segment length limit");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\" + new string('中', 86) + ".txt", out error)
            && error.IndexOf("255") >= 0, "validate: UTF-8 segment byte limit");
        Check(!CoreUnderTest.ValidateCrossPlatformPath(@"C:\A\B" + ((char)1) + "C.TXT", out error),
            "validate: control character");
    }

    private static void TestDateResolution()
    {
        Dictionary<string, DateTime?> dates = new Dictionary<string, DateTime?>();
        dates["filename"] = null;
        dates["exif"] = new DateTime(2020, 6, 15);
        dates["modified"] = new DateTime(2021, 1, 1);
        dates["created"] = new DateTime(2019, 1, 1);

        DateTime? effective;
        string source;
        string missing;
        bool skipped;
        CoreUnderTest.ResolveDateValue(dates, "filename",
            "filename,exif,modified,created,empty", "自定义值",
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15) && source == "exif" && !skipped,
            "date resolve: fallback to exif");

        CoreUnderTest.ResolveDateValue(dates, "exif",
            "filename,exif,modified,created,empty", "自定义值",
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15) && source == "exif",
            "date resolve: primary wins");

        dates["exif"] = null;
        CoreUnderTest.ResolveDateValue(dates, "exif",
            "filename,empty,modified,created,empty", "自定义值",
            out effective, out source, out missing, out skipped);
        Check(!effective.HasValue && source == "empty" && missing.Length == 0,
            "date resolve: empty stops fallback");

        CoreUnderTest.ResolveDateValue(dates, "exif",
            "custom,empty,empty,empty,empty", "缺失文本",
            out effective, out source, out missing, out skipped);
        Check(!effective.HasValue && source == "custom" && missing == "缺失文本",
            "date resolve: custom missing text");

        CoreUnderTest.ResolveDateValue(dates, "exif",
            "skip,empty,empty,empty,empty", "缺失文本",
            out effective, out source, out missing, out skipped);
        Check(skipped && source == "skip", "date resolve: skip file");

        Dictionary<string, DateTime?> emptyDates = new Dictionary<string, DateTime?>();
        emptyDates["filename"] = null;
        emptyDates["exif"] = null;
        emptyDates["modified"] = null;
        emptyDates["created"] = null;
        CoreUnderTest.ResolveDateValue(emptyDates, "filename",
            "filename,exif,modified,created,empty", "",
            out effective, out source, out missing, out skipped);
        Check(!effective.HasValue && source == "empty" && missing.Length == 0 && !skipped,
            "date resolve: all missing defaults to empty");

        CheckEqual("filename,exif,modified,created,empty",
            CoreUnderTest.NormalizeFallbackOrder(""), "date resolve: default five slots");
        CheckEqual("skip,custom,empty,filename,created",
            CoreUnderTest.NormalizeFallbackOrder("跳过,自定义,空值,文件名,创建"),
            "date resolve: special tokens and aliases");

        Dictionary<string, DateTime?> conflictDates = new Dictionary<string, DateTime?>();
        conflictDates["filename"] = new DateTime(2020, 1, 2);
        conflictDates["exif"] = new DateTime(2020, 6, 15, 10, 30, 0);
        conflictDates["modified"] = new DateTime(2021, 1, 1);
        conflictDates["created"] = new DateTime(2019, 1, 1);

        CoreUnderTest.ResolveDateValue(conflictDates, "filename",
            "filename,exif,modified,created,empty", "", false,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 1, 2) && source == "filename",
            "date resolve: filename kept when prefer-exif off");

        CoreUnderTest.ResolveDateValue(conflictDates, "filename",
            "filename,exif,modified,created,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15, 10, 30, 0) && source == "exif",
            "date resolve: prefer exif on filename conflict");

        CoreUnderTest.ResolveDateValue(conflictDates, "modified",
            "filename,exif,modified,created,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2021, 1, 1) && source == "modified",
            "date resolve: prefer-exif does not override other primary source");

        Dictionary<string, DateTime?> sameDate = new Dictionary<string, DateTime?>();
        sameDate["filename"] = new DateTime(2020, 6, 15);
        sameDate["exif"] = new DateTime(2020, 6, 15, 23, 59, 0);
        sameDate["modified"] = null;
        sameDate["created"] = null;
        CoreUnderTest.ResolveDateValue(sameDate, "filename",
            "filename,exif,modified,created,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15) && source == "filename",
            "date resolve: same calendar date is not a conflict");

        Dictionary<string, DateTime?> fallbackConflict = new Dictionary<string, DateTime?>();
        fallbackConflict["filename"] = new DateTime(2020, 1, 2);
        fallbackConflict["exif"] = new DateTime(2020, 6, 15);
        fallbackConflict["modified"] = null;
        fallbackConflict["created"] = null;
        CoreUnderTest.ResolveDateValue(fallbackConflict, "modified",
            "filename,exif,empty,empty,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15) && source == "exif",
            "date resolve: prefer exif when filename is fallback");

        conflictDates["exif"] = null;
        CoreUnderTest.ResolveDateValue(conflictDates, "filename",
            "filename,exif,modified,created,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 1, 2) && source == "filename",
            "date resolve: keep filename when exif missing");

        // 四值重载：批量刷新日期时不再为每行构造 Dictionary。
        CoreUnderTest.ResolveDateValue(new DateTime(2020, 1, 2),
            new DateTime(2020, 6, 15, 10, 30, 0), null, null,
            "filename", "filename,exif,modified,created,empty", "", true,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2020, 6, 15, 10, 30, 0) && source == "exif",
            "date resolve values: prefer exif on conflict");

        CoreUnderTest.ResolveDateValue(new DateTime(2020, 1, 2),
            new DateTime(2020, 6, 15), new DateTime(2021, 1, 1), new DateTime(2019, 1, 1),
            "modified", "filename,exif,modified,created,empty", "", false,
            out effective, out source, out missing, out skipped);
        Check(effective == new DateTime(2021, 1, 1) && source == "modified",
            "date resolve values: primary modified");

        CoreUnderTest.ResolveDateValue(null, null, null, null,
            "filename", "custom,empty,empty,empty,empty", "缺失文本", false,
            out effective, out source, out missing, out skipped);
        Check(!effective.HasValue && source == "custom" && missing == "缺失文本",
            "date resolve values: custom fallback");
    }

    private static void TestFilter()
    {
        CheckEqual("jpg;png;mp4",
            CoreUnderTest.NormalizeExtensionList("*.JPG, .png | mp4 jpg"),
            "filter: normalize extension list");
        Check(CoreUnderTest.ShouldSkipByFilter("jpg", "jpg;png", "skip"),
            "filter: skip matching");
        Check(!CoreUnderTest.ShouldSkipByFilter("txt", "jpg;png", "skip"),
            "filter: keep non-matching in skip mode");
        Check(CoreUnderTest.ShouldSkipByFilter("txt", "jpg;png", "execute"),
            "filter: legacy execute becomes rename whitelist");
        Check(!CoreUnderTest.ShouldSkipByFilter("png", "jpg;png", "execute"),
            "filter: keep matching in rename whitelist");
        Check(!CoreUnderTest.ShouldSkipByFilter("txt", "", "rename"),
            "filter: empty list does not filter");
        Check(CoreUnderTest.ShouldSkipByFilter("txt", "*", "skip"),
            "filter: wildcard matches all in skip mode");
        Check(!CoreUnderTest.ShouldSkipByFilter("txt", "*", "rename"),
            "filter: wildcard matches all in rename mode");
        CheckEqual("skip", CoreUnderTest.NormalizeFilterMode("跳过"), "filter: mode alias skip");
        CheckEqual("copy", CoreUnderTest.NormalizeFilterMode("复制"), "filter: mode alias copy");
        CheckEqual("move", CoreUnderTest.NormalizeFilterMode("移动"), "filter: mode alias move");
        CheckEqual("rename", CoreUnderTest.NormalizeFilterMode("重命名"), "filter: mode alias rename");
        CheckEqual("重命名", CoreUnderTest.ResolveAction("rename", true, false, true),
            "action: rename same directory");
        CheckEqual("复制", CoreUnderTest.ResolveAction("rename", true, false, false),
            "action: rename becomes copy when directory changes");
        CheckEqual("复制", CoreUnderTest.ResolveAction("copy", true, true, true),
            "action: copy mode");
        CheckEqual("移动", CoreUnderTest.ResolveAction("move", true, true, true),
            "action: move mode");
        CheckEqual("跳过", CoreUnderTest.ResolveAction("copy", false, true, true),
            "action: non-matching is skipped in whitelist mode");
        CheckEqual("跳过", CoreUnderTest.ResolveAction("skip", true, true, true),
            "action: matching is skipped in blacklist mode");
        CheckEqual("重命名", CoreUnderTest.ResolveAction("skip", false, true, true),
            "action: non-matching keeps rename in blacklist mode");
        CheckEqual("复制", CoreUnderTest.ResolveAction("skip", false, true, false),
            "action: non-matching becomes copy when directory changes");
    }

    private static void TestTransferEngine()
    {
        string dir = Path.Combine(Path.GetTempPath(), "qk-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Check(CoreUnderTest.IsNetworkTransferPath(@"\\server\share\a.txt"),
                "transfer: UNC detected as network");
            CheckEqual("network:262144",
                CoreUnderTest.DescribeTransferProfile(@"\\server\share\a.txt", 1024),
                "transfer: network buffer profile");
            CheckEqual(262144, CoreUnderTest.SelectCopyBufferSize(@"\\server\share\a.txt", 1024),
                "transfer: network buffer size");
            CheckEqual(262144, CoreUnderTest.SelectCopyBufferSizeForDrive(DriveType.Network, 1024),
                "transfer: network drive profile");
            CheckEqual(65536, CoreUnderTest.SelectCopyBufferSizeForDrive(DriveType.CDRom, 1024),
                "transfer: cdrom drive profile");
            CheckEqual(1048576, CoreUnderTest.SelectCopyBufferSizeForDrive(DriveType.Removable, 1024),
                "transfer: removable small file profile");
            CheckEqual(2097152, CoreUnderTest.SelectCopyBufferSizeForDrive(
                DriveType.Removable, 600L * 1024 * 1024), "transfer: removable large file profile");
            CheckEqual(1048576, CoreUnderTest.SelectCopyBufferSizeForDrive(DriveType.Fixed, 1024),
                "transfer: fixed small file profile");
            CheckEqual(4194304, CoreUnderTest.SelectCopyBufferSizeForDrive(
                DriveType.Fixed, 2L * 1024 * 1024 * 1024), "transfer: fixed large file profile");
            CheckEqual(4194304, CoreUnderTest.SelectCopyBufferSizeForDrive(
                DriveType.Unknown, 2L * 1024 * 1024 * 1024), "transfer: unknown large file profile");
            Check(CoreUnderTest.SelectCopyBufferSize(Path.Combine(dir, "a.bin"), 2L * 1024 * 1024 * 1024) >=
                CoreUnderTest.TransferBufferDefault, "transfer: large local buffer");
            Check(CoreUnderTest.IsSameVolumePath(Path.Combine(dir, "a.txt"), Path.Combine(dir, "b.txt")),
                "transfer: same directory is same volume");
            CheckEqual(200, CoreUnderTest.GetTransferRetryDelayMs(1), "transfer: retry delay 1");
            CheckEqual(400, CoreUnderTest.GetTransferRetryDelayMs(2), "transfer: retry delay 2");
            CheckEqual(2000, CoreUnderTest.GetTransferRetryDelayMs(9), "transfer: retry delay cap");
            Check(CoreUnderTest.IsTransientTransferError(32), "transfer: sharing violation is transient");
            Check(!CoreUnderTest.IsTransientTransferError(2), "transfer: missing file is not transient");
            CheckEqual("磁盘空间不足", CoreUnderTest.DescribeTransferError(112),
                "transfer: disk full message");
            CheckEqual("网络传输超时", CoreUnderTest.DescribeTransferError(121),
                "transfer: network timeout message");

            string source = Path.Combine(dir, "source.bin");
            string target = Path.Combine(dir, "target.bin");
            byte[] data = new byte[2 * 1024 * 1024 + 123];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i % 251);
            File.WriteAllBytes(source, data);
            DateTime stamp = new DateTime(2020, 2, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, stamp);

            long reported = 0;
            Action<long> progress = delegate(long delta) { reported += delta; };
            string copyError;
            Check(CoreUnderTest.CopyFileWithRetry(source, target, progress, null, 3, out copyError),
                "transfer: copy succeeds");
            CheckEqual("", copyError == null ? "" : copyError, "transfer: copy no error");
            Check(File.Exists(target), "transfer: copy target exists");
            CheckEqual(Convert.ToBase64String(data), Convert.ToBase64String(File.ReadAllBytes(target)),
                "transfer: copy content matches");
            CheckEqual(data.Length.ToString(), reported.ToString(), "transfer: copy progress total");
            Check(File.GetLastWriteTimeUtc(target) == stamp, "transfer: copy preserves timestamp");
            Check(File.GetAttributes(target) == File.GetAttributes(source),
                "transfer: copy preserves attributes");
            Check(Directory.GetFiles(dir, ".qk-*.tmp").Length == 0, "transfer: no temp files left");

            string existingError;
            Check(!CoreUnderTest.CopyFileWithRetry(source, target, null, null, 1, out existingError),
                "transfer: existing target rejected");
            Check(existingError != null && existingError.Contains("已存在"),
                "transfer: existing target message");

            string missingError;
            Check(!CoreUnderTest.CopyFileWithRetry(Path.Combine(dir, "missing.bin"),
                Path.Combine(dir, "missing-out.bin"), null, null, 1, out missingError),
                "transfer: missing source rejected");
            Check(missingError != null && missingError.Contains("不存在"),
                "transfer: missing source message");

            string cancelTarget = Path.Combine(dir, "cancel.bin");
            string cancelError;
            Check(!CoreUnderTest.CopyFileWithRetry(source, cancelTarget, null,
                delegate() { return true; }, 1, out cancelError),
                "transfer: cancel aborts copy");
            Check(cancelError != null && cancelError.Contains("取消"),
                "transfer: cancel message");
            Check(!File.Exists(cancelTarget), "transfer: cancel leaves no target");

            string moveSource = Path.Combine(dir, "move-source.bin");
            string moveTarget = Path.Combine(dir, "move-target.bin");
            File.WriteAllBytes(moveSource, new byte[] { 1, 2, 3, 4 });
            string moveError;
            Check(CoreUnderTest.MoveFileWithRetry(moveSource, moveTarget, 3, out moveError),
                "transfer: move succeeds");
            Check(!File.Exists(moveSource) && File.Exists(moveTarget),
                "transfer: move source removed and target exists");

            Check(CoreUnderTest.CheckFreeSpace(Path.Combine(dir, "space.bin"), 1024, out copyError),
                "transfer: free space check for small file");
            Check(CoreUnderTest.IsFileUnchanged(source, data.Length, stamp),
                "transfer: source stability check");
            File.SetLastWriteTimeUtc(source, stamp.AddMinutes(1));
            Check(!CoreUnderTest.IsFileUnchanged(source, data.Length, stamp),
                "transfer: source change detected");
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
    }

    private static void TestPreset()
    {
        Dictionary<string, string> config = CoreUnderTest.CreateDefaultConfig(2000);
        config["rootMode"] = "custom";
        config["rootPath"] = @"D:\Out";
        config["startYear"] = "2010";
        config["filterExtensions"] = "jpg;png";
        config["filterMode"] = "move";
        config["dateSource"] = "exif";
        config["dateFallbackOrder"] = "exif,created,custom,empty,skip";
        config["dateFallbackCustomText"] = "兜底日期";
        config["preferExifOnFilenameConflict"] = "true";
        config["realTimePreview"] = "false";
        config["execParallel"] = "5";
        config["execVerify"] = "false";
        config["checksumAlgorithm"] = "SHA-256";
        config["checksumDigits"] = "16";
        config["checksumPrefix"] = "P";
        config["checksumSuffix"] = "S";
        config["template"] = "【根路径】/X-【校验码】.【扩展名】";

        Dictionary<string, object> preset = CoreUnderTest.BuildPresetDictionary(config);
        Check(Convert.ToString(preset["presetVersion"]) == "0.05", "preset: version");
        Check(Convert.ToString(preset["rootMode"]) == "custom", "preset: root mode");
        Check(Convert.ToString(preset["checksumDigits"]) == "16", "preset: digits");
        Check(Convert.ToString(preset["filterExtensions"]) == "jpg;png", "preset: filter extensions");
        Check(Convert.ToString(preset["filterMode"]) == "move", "preset: filter mode");
        Check(Convert.ToString(preset["dateFallbackOrder"]) == "exif,created,custom,empty,skip", "preset: fallback order");
        Check(Convert.ToString(preset["dateFallbackCustomText"]) == "兜底日期", "preset: fallback custom text");
        Check(Convert.ToString(preset["preferExifOnFilenameConflict"]) == "true", "preset: prefer exif flag");
        Check(Convert.ToString(preset["realTimePreview"]) == "false", "preset: realtime flag");
        Check(Convert.ToString(preset["execParallel"]) == "5", "preset: exec parallel");
        Check(Convert.ToString(preset["execVerify"]) == "false", "preset: exec verify");

        Dictionary<string, string> loaded = CoreUnderTest.CreateDefaultConfig(2000);
        bool applied = CoreUnderTest.ApplyPresetDictionary(preset, loaded);
        Check(applied, "preset: applied");
        CheckEqual(@"D:\Out", loaded["rootPath"], "preset: root path");
        CheckEqual("2010", loaded["startYear"], "preset: start year");
        CheckEqual("exif", loaded["dateSource"], "preset: date source");
        CheckEqual("jpg;png", loaded["filterExtensions"], "preset: filter extensions roundtrip");
        CheckEqual("move", loaded["filterMode"], "preset: filter mode roundtrip");
        CheckEqual("exif,created,custom,empty,skip", loaded["dateFallbackOrder"], "preset: order roundtrip");
        CheckEqual("兜底日期", loaded["dateFallbackCustomText"], "preset: fallback custom roundtrip");
        CheckEqual("true", loaded["preferExifOnFilenameConflict"], "preset: prefer exif roundtrip");
        CheckEqual("false", loaded["realTimePreview"], "preset: realtime roundtrip");
        CheckEqual("5", loaded["execParallel"], "preset: exec parallel roundtrip");
        CheckEqual("false", loaded["execVerify"], "preset: exec verify roundtrip");
        CheckEqual("SHA-256", loaded["checksumAlgorithm"], "preset: algorithm");
        CheckEqual("16", loaded["checksumDigits"], "preset: digits roundtrip");
        CheckEqual("P", loaded["checksumPrefix"], "preset: prefix");
        CheckEqual("S", loaded["checksumSuffix"], "preset: suffix");
        CheckEqual("【根路径】/X-【校验码】.【扩展名】", loaded["template"], "preset: template");

        Dictionary<string, string> defaults = CoreUnderTest.CreateDefaultConfig(2000);
        CheckEqual("[", defaults["checksumPrefix"], "preset: default prefix is [");
        CheckEqual("]", defaults["checksumSuffix"], "preset: default suffix is ]");
        CheckEqual("", defaults["filterExtensions"], "preset: default filter empty");
        CheckEqual("skip", defaults["filterMode"], "preset: default filter mode");
        CheckEqual("filename,exif,modified,created,empty", defaults["dateFallbackOrder"], "preset: default fallback order");
        CheckEqual("", defaults["dateFallbackCustomText"], "preset: default fallback custom empty");
        CheckEqual("false", defaults["preferExifOnFilenameConflict"], "preset: default prefer exif off");
        CheckEqual("true", defaults["realTimePreview"], "preset: default realtime");
        CheckEqual("2", defaults["execParallel"], "preset: default exec parallel");
        CheckEqual("true", defaults["execVerify"], "preset: default exec verify");

        Dictionary<string, string> partial = CoreUnderTest.CreateDefaultConfig(2000);
        Dictionary<string, object> p2 = new Dictionary<string, object>();
        p2["checksumAlgorithm"] = "CRC32";
        p2["checksumDigits"] = "99";
        p2["execParallel"] = "99";
        Check(CoreUnderTest.ApplyPresetDictionary(p2, partial), "preset: partial applied");
        CheckEqual("8", partial["checksumDigits"], "preset: digits clamped to algorithm max");
        CheckEqual("8", partial["execParallel"], "preset: exec parallel clamped to 8");

        Dictionary<string, string> aliases = CoreUnderTest.CreateDefaultConfig(2000);
        Dictionary<string, object> p3 = new Dictionary<string, object>();
        p3["dateSource"] = "修改日期";
        p3["checksumAlgorithm"] = "sha256";
        p3["rootMode"] = "其他文件夹";
        p3["filterMode"] = "execute";
        Check(CoreUnderTest.ApplyPresetDictionary(p3, aliases), "preset: aliases applied");
        CheckEqual("modified", aliases["dateSource"], "preset: Chinese date alias");
        CheckEqual("SHA-256", aliases["checksumAlgorithm"], "preset: algorithm alias");
        CheckEqual("custom", aliases["rootMode"], "preset: root alias");
        CheckEqual("rename", aliases["filterMode"], "preset: legacy execute migrates to rename");
        CheckEqual(CoreUnderTest.RenameDefaultTemplate, aliases["template"],
            "preset: rename mode without template uses rename default");

        Dictionary<string, string> renameConfig = CoreUnderTest.CreateDefaultConfig(2000);
        renameConfig["filterMode"] = "rename";
        CoreUnderTest.NormalizeConfig(renameConfig);
        CheckEqual(CoreUnderTest.DefaultTemplate, renameConfig["template"],
            "preset: explicit generic template is preserved in rename mode");
    }

    private static void TestPresetJsonState()
    {
        string presetJson = "{\"presetVersion\":\"0.05\",\"presets\":{\"图片-截屏\":{\"presetVersion\":\"0.05\",\"rootMode\":\"current\",\"rootPath\":\"\",\"startYear\":\"2000\",\"filterExtensions\":\"\",\"filterMode\":\"skip\",\"dateSource\":\"filename\",\"dateFallbackOrder\":\"filename,exif,modified,created,empty\",\"dateFallbackCustomText\":\"\",\"realTimePreview\":\"true\",\"execParallel\":\"2\",\"execVerify\":\"true\",\"checksumAlgorithm\":\"CRC32\",\"checksumDigits\":\"8\",\"checksumPrefix\":\"[\",\"checksumSuffix\":\"]\",\"template\":\"【根路径】/【日期yyyy】.【日期mm】/图片/截屏/【源文件名】-【校验码】.【扩展名】\"}}}";

        object parsed;
        Check(CoreUnderTest.TryParseJson(presetJson, out parsed), "preset json: parse nested state");
        IDictionary<string, object> root = parsed as IDictionary<string, object>;
        Check(root != null, "preset json: root dictionary");

        IDictionary<string, object> presetConfig = null;
        if (root != null)
        {
            object presetsObject;
            Check(root.TryGetValue("presets", out presetsObject), "preset json: presets key");
            IDictionary<string, object> presets = presetsObject as IDictionary<string, object>;
            Check(presets != null && presets.ContainsKey("图片-截屏"), "preset json: preset name");
            if (presets != null && presets.ContainsKey("图片-截屏"))
                presetConfig = presets["图片-截屏"] as IDictionary<string, object>;
            Check(presetConfig != null, "preset json: config dictionary");
        }

        if (presetConfig != null)
        {
            CheckEqual("CRC32", Convert.ToString(presetConfig["checksumAlgorithm"]), "preset json: checksum");
            CheckEqual("【根路径】/【日期yyyy】.【日期mm】/图片/截屏/【源文件名】-【校验码】.【扩展名】",
                Convert.ToString(presetConfig["template"]), "preset json: template");

            Dictionary<string, string> loaded = CoreUnderTest.CreateDefaultConfig(2000);
            Check(CoreUnderTest.ApplyPresetDictionary(presetConfig, loaded), "preset json: apply parsed config");
            CheckEqual("CRC32", loaded["checksumAlgorithm"], "preset json: applied checksum");
        }

        object escaped;
        Check(CoreUnderTest.TryParseJson("{\"name\":\"a\\\"b\\\\c\\u56FE\"}", out escaped),
            "preset json: parse escapes");
        IDictionary<string, object> escapedDictionary = escaped as IDictionary<string, object>;
        Check(escapedDictionary != null &&
            Convert.ToString(escapedDictionary["name"]) == "a\"b\\c图",
            "preset json: escape values");

        object arrayValue;
        Check(CoreUnderTest.TryParseJson("[1,2,\"x\",true,null]", out arrayValue) &&
            arrayValue is List<object> && ((List<object>)arrayValue).Count == 5,
            "preset json: array values");

        object invalid;
        Check(!CoreUnderTest.TryParseJson("{\"presets\":", out invalid), "preset json: reject truncated");
        Check(!CoreUnderTest.TryParseJson("", out invalid), "preset json: reject empty");
        Check(!CoreUnderTest.TryParseJson("not json", out invalid), "preset json: reject text");
    }

    private static void TestFlatPresetStore()
    {
        Dictionary<string, string> config = CoreUnderTest.CreateDefaultConfig(2000);
        config["rootMode"] = "custom";
        config["rootPath"] = @"D:\输出";
        config["startYear"] = "1999";
        config["filterExtensions"] = "jpg;png";
        config["filterMode"] = "rename";
        config["dateSource"] = "exif";
        config["dateFallbackOrder"] = "filename,modified,created,empty,skip";
        config["dateFallbackCustomText"] = "无日期";
        config["preferExifOnFilenameConflict"] = "true";
        config["realTimePreview"] = "false";
        config["execParallel"] = "6";
        config["execVerify"] = "false";
        config["checksumAlgorithm"] = "SHA-256";
        config["checksumDigits"] = "16";
        config["checksumPrefix"] = "<";
        config["checksumSuffix"] = ">";
        config["template"] = "【根路径】/【源文件名】-【校验码】.【扩展名】";

        Dictionary<string, object> preset = CoreUnderTest.BuildPresetDictionary(config);
        for (int i = 0; i < CoreUnderTest.PresetConfigKeys.Length; i++)
        {
            string key = CoreUnderTest.PresetConfigKeys[i];
            Check(config.ContainsKey(key) && preset.ContainsKey(key),
                "flat preset: config key present " + key);
        }
        foreach (string key in preset.Keys)
        {
            if (key == "presetVersion")
                continue;
            Check(config.ContainsKey(key), "flat preset: no extra preset key " + key);
        }

        Dictionary<string, Dictionary<string, object>> store =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        store["默认"] = preset;
        store["图片\"截屏\\测试"] = CoreUnderTest.BuildPresetDictionary(
            CoreUnderTest.CreateDefaultConfig(2000));

        Dictionary<string, object> flat = CoreUnderTest.BuildFlatPresetDictionary(store);
        Check(flat.Count == 2, "flat preset: stores two presets");
        Check(flat["默认"] is string, "flat preset: value is json string");
        string defaultJson = Convert.ToString(flat["默认"]);
        Check(defaultJson.Contains("\"rootMode\":\"custom\""), "flat preset: json contains root mode");
        Check(defaultJson.Contains("D:\\\\输出"), "flat preset: json escapes backslash");
        Check(defaultJson.Contains("无日期"), "flat preset: json keeps chinese");

        Dictionary<string, Dictionary<string, object>> parsed =
            CoreUnderTest.ParsePresetDictionary(flat);
        Check(parsed.Count == 2 && parsed.ContainsKey("默认") &&
            parsed.ContainsKey("图片\"截屏\\测试"), "flat preset: parse names");

        Dictionary<string, string> loaded = CoreUnderTest.CreateDefaultConfig(2000);
        Check(CoreUnderTest.ApplyPresetDictionary(parsed["默认"], loaded),
            "flat preset: apply parsed default");
        for (int i = 0; i < CoreUnderTest.PresetConfigKeys.Length; i++)
        {
            string key = CoreUnderTest.PresetConfigKeys[i];
            CheckEqual(config[key], loaded[key], "flat preset: roundtrip " + key);
        }

        Dictionary<string, object> oldRoot = new Dictionary<string, object>();
        oldRoot["presetVersion"] = "0.05";
        oldRoot["presets"] = store;
        Dictionary<string, Dictionary<string, object>> oldParsed =
            CoreUnderTest.ParsePresetDictionary(oldRoot);
        Check(oldParsed.Count == 2 && oldParsed.ContainsKey("默认"),
            "flat preset: migrate old nested structure");

        Dictionary<string, Dictionary<string, object>> legacyParsed =
            CoreUnderTest.ParsePresetDictionary(preset);
        Check(legacyParsed.Count == 1 && legacyParsed.ContainsKey("默认"),
            "flat preset: migrate legacy single preset");

        Dictionary<string, object> stringState = new Dictionary<string, object>();
        stringState["默认"] = defaultJson;
        Check(CoreUnderTest.ParsePresetDictionary(stringState).ContainsKey("默认"),
            "flat preset: parse json string value");

        string escaped = CoreUnderTest.SerializeJsonValue("a\"b\\c\n图");
        object escapedParsed;
        Check(CoreUnderTest.TryParseJson(escaped, out escapedParsed) &&
            Convert.ToString(escapedParsed) == "a\"b\\c\n图",
            "flat preset: json serializer escapes text");
    }

    private static void TestPresetStructureCompatibility()
    {
        Dictionary<string, string> config = CoreUnderTest.CreateDefaultConfig(2000);
        Dictionary<string, object> single = CoreUnderTest.BuildPresetDictionary(config);
        Dictionary<string, Dictionary<string, object>> store =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        store["默认"] = single;

        Dictionary<string, object> nestedWithoutVersion = new Dictionary<string, object>();
        nestedWithoutVersion["presets"] = store;
        Dictionary<string, Dictionary<string, object>> parsed =
            CoreUnderTest.ParsePresetDictionary(nestedWithoutVersion);
        Check(parsed.Count == 1 && parsed.ContainsKey("默认"),
            "preset compat: nested without presetVersion");

        Dictionary<string, object> flatNamedTemplate = new Dictionary<string, object>();
        flatNamedTemplate["template"] = CoreUnderTest.SerializeJsonValue(single);
        parsed = CoreUnderTest.ParsePresetDictionary(flatNamedTemplate);
        Check(parsed.Count == 1 && parsed.ContainsKey("template"),
            "preset compat: preset named like config key");

        Dictionary<string, object> flatNamedPresets = new Dictionary<string, object>();
        flatNamedPresets["presets"] = CoreUnderTest.SerializeJsonValue(single);
        parsed = CoreUnderTest.ParsePresetDictionary(flatNamedPresets);
        Check(parsed.Count == 1 && parsed.ContainsKey("presets"),
            "preset compat: preset named presets");

        parsed = CoreUnderTest.ParsePresetDictionary(single);
        Check(parsed.Count == 1 && parsed.ContainsKey("默认"),
            "preset compat: legacy single config");
    }

    private static void TestExtensionPresetStore()
    {
        Dictionary<string, string> store =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        store["图片"] = "jpg;png";
        store["文档"] = "doc;docx";

        Dictionary<string, object> flat = CoreUnderTest.BuildFlatExtensionPresetDictionary(store);
        Check(flat.Count == 2, "ext preset: flat count");
        Check(flat["图片"] is string, "ext preset: flat value is plain string");

        Dictionary<string, string> parsed = CoreUnderTest.ParseExtensionPresetDictionary(flat);
        Check(parsed.Count == 2, "ext preset: parse flat count");
        CheckEqual("jpg;png", parsed["图片"], "ext preset: parse flat value");
        CheckEqual("doc;docx", parsed["文档"], "ext preset: parse second value");

        Dictionary<string, object> legacy = new Dictionary<string, object>();
        legacy["presetVersion"] = "0.05";
        Dictionary<string, object> presets = new Dictionary<string, object>();
        presets["图片"] = "jpg;png";
        legacy["presets"] = presets;
        parsed = CoreUnderTest.ParseExtensionPresetDictionary(legacy);
        Check(parsed.Count == 1 && parsed.ContainsKey("图片"),
            "ext preset: migrate legacy nested");
        Check(!parsed.ContainsKey("presetVersion"), "ext preset: ignore version key");

        object jsonValue;
        Check(CoreUnderTest.TryParseJson(CoreUnderTest.SerializeJsonValue(flat), out jsonValue),
            "ext preset: serialize flat store");
        parsed = CoreUnderTest.ParseExtensionPresetDictionary(jsonValue);
        Check(parsed.Count == 2, "ext preset: parse serialized store");
    }

    // 第一性原理评审补充的边界与复用回归测试。
    private static void TestCornerCases()
    {
        // 日期来源别名必须与递补解析使用同一套映射。
        CheckEqual("modified", CoreUnderTest.NormalizeDateSource("修改"),
            "corner: date source 修改 alias");
        CheckEqual("created", CoreUnderTest.NormalizeDateSource("创建"),
            "corner: date source 创建 alias");
        CheckEqual("modified", CoreUnderTest.NormalizeDateSource(" 修改日期 "),
            "corner: date source trims and lowercases");
        CheckEqual("exif", CoreUnderTest.NormalizeDateSource("EXIF拍摄日期"),
            "corner: date source EXIF alias");
        CheckEqual("filename", CoreUnderTest.NormalizeDateSource("未知来源"),
            "corner: date source unknown falls back to filename");

        // 校验位数钳制在界面、配置和徽标三处必须一致。
        CheckEqual(1, CoreUnderTest.ClampChecksumDigits("CRC32", 0),
            "corner: digits zero clamps to 1");
        CheckEqual(1, CoreUnderTest.ClampChecksumDigits("CRC32", -9),
            "corner: digits negative clamps to 1");
        CheckEqual(8, CoreUnderTest.ClampChecksumDigits("CRC32", 9),
            "corner: digits above CRC32 max");
        CheckEqual(64, CoreUnderTest.ClampChecksumDigits("sha256", 999),
            "corner: digits above SHA-256 max");
        CheckEqual(1, CoreUnderTest.ClampChecksumDigits("unknown", 5),
            "corner: unknown algorithm clamps to 1");
        CheckEqual("CRC32_4-…",
            CoreUnderTest.BuildPendingChecksumBadge("CRC32", 4, null, null),
            "corner: pending badge null affixes");
        CheckEqual("MD5_4-aaaa",
            CoreUnderTest.BuildChecksumBadge("MD5", 4, null, null, "aaaabbbb"),
            "corner: badge null affixes");

        // 扩展名列表去重、通配符与分隔符兼容。
        CheckEqual("jpg;png", CoreUnderTest.NormalizeExtensionList("*.JPG; ..PNG | *.jpg"),
            "corner: extension list dedupes and strips wildcards");
        CheckEqual("", CoreUnderTest.NormalizeExtensionList(null),
            "corner: extension list null is empty");
        CheckEqual("*", CoreUnderTest.NormalizeExtensionList("*.*"),
            "corner: extension list all wildcard");

        // 配置与预设中的越界位数必须被规范化。
        Dictionary<string, string> config = CoreUnderTest.CreateDefaultConfig(2000);
        config["checksumDigits"] = "999";
        CoreUnderTest.NormalizeConfig(config);
        CheckEqual("8", config["checksumDigits"], "corner: config digits clamp high");
        config["checksumDigits"] = "-5";
        CoreUnderTest.NormalizeConfig(config);
        CheckEqual("1", config["checksumDigits"], "corner: config digits clamp low");

        config = CoreUnderTest.CreateDefaultConfig(2000);
        Check(!CoreUnderTest.ApplyPresetDictionary(null, config),
            "corner: null preset rejected");
        Check(!CoreUnderTest.ApplyPresetDictionary(new Dictionary<string, object>(), config),
            "corner: empty preset rejected");
        Dictionary<string, object> preset = new Dictionary<string, object>();
        preset["checksumDigits"] = "999";
        Check(CoreUnderTest.ApplyPresetDictionary(preset, config),
            "corner: preset with digits applies");
        CheckEqual("8", config["checksumDigits"], "corner: preset digits clamped");

        // 模板词典缓冲区复用时每次都必须覆盖全部键。
        Dictionary<string, string> buffer = new Dictionary<string, string>();
        string target;
        string error;
        Check(CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\root",
            null, "", "a", "txt", "", buffer, out target, out error)
            && target == @"C:\root\a.txt", "corner: reusable template buffer first call");
        Check(CoreUnderTest.TryBuildTargetPath("【根路径】/【日期yyyy】-【源文件名】.【扩展名】", @"C:\root",
            new DateTime(2021, 3, 4), "", "b", "txt", "", buffer, out target, out error)
            && target == @"C:\root\2021-b.txt", "corner: reusable template buffer overwrites");
        Check(buffer.Count == 7, "corner: reusable template buffer has 7 keys");

        // JSON深度上限：深层嵌套必须失败而不是栈溢出。
        StringBuilder deep = new StringBuilder();
        for (int i = 0; i < 80; i++) deep.Append('[');
        deep.Append('1');
        for (int i = 0; i < 80; i++) deep.Append(']');
        object parsed;
        Check(!CoreUnderTest.TryParseJson(deep.ToString(), out parsed),
            "corner: json depth limit rejects");
        StringBuilder shallow = new StringBuilder();
        for (int i = 0; i < 10; i++) shallow.Append('[');
        shallow.Append('1');
        for (int i = 0; i < 10; i++) shallow.Append(']');
        Check(CoreUnderTest.TryParseJson(shallow.ToString(), out parsed),
            "corner: json shallow nesting accepted");

        // 传输错误码映射与目标已存在的友好提示。
        CheckEqual("目标文件已存在", CoreUnderTest.DescribeTransferError(183),
            "corner: transfer error 183 friendly");
        CheckEqual("目标文件已存在", CoreUnderTest.DescribeTransferError(80),
            "corner: transfer error 80 friendly");
        Check(CoreUnderTest.IsTransientTransferError(32),
            "corner: sharing violation is transient");
        Check(!CoreUnderTest.IsTransientTransferError(2),
            "corner: file not found is not transient");

        string dir = Path.Combine(Path.GetTempPath(), "qk-corner-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(dir, "source.txt");
        string existing = Path.Combine(dir, "existing.txt");
        string missing = Path.Combine(dir, "missing.txt");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(source, "source");
            File.WriteAllText(existing, "existing");

            string moveError;
            Check(!CoreUnderTest.MoveFileWithRetry(source, existing, 1, out moveError),
                "corner: move rejects existing target");
            CheckEqual("目标文件已存在", moveError, "corner: move existing target message");
            Check(File.Exists(source), "corner: move keeps source on existing target");

            Check(!CoreUnderTest.MoveFileWithRetry(missing, Path.Combine(dir, "x.txt"), 1, out moveError),
                "corner: move rejects missing source");
            CheckEqual("源文件不存在", moveError, "corner: move missing source message");

            string copyError;
            int copyCode;
            Check(!CoreUnderTest.CopyFileAdaptive(source, existing, null, null,
                out copyError, out copyCode), "corner: copy rejects existing target");
            CheckEqual(80, copyCode, "corner: copy existing target code");

            string spaceError;
            Check(CoreUnderTest.CheckFreeSpace(Path.GetTempPath(), 0, out spaceError),
                "corner: zero required space passes");
            Check(CoreUnderTest.CheckFreeSpace(Path.GetTempPath(), -1, out spaceError),
                "corner: negative required space passes");
        }
        finally
        {
            try { File.Delete(source); } catch { }
            try { File.Delete(existing); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }

        // 云端占位属性识别：命中任一云属性即视为内容不在本地。
        Check(CoreUnderTest.IsCloudPlaceholderAttributes(CoreUnderTest.FileAttributeOffline),
            "corner: offline attribute is cloud placeholder");
        Check(CoreUnderTest.IsCloudPlaceholderAttributes(CoreUnderTest.FileAttributeRecallOnDataAccess),
            "corner: recall-on-data-access is cloud placeholder");
        Check(CoreUnderTest.IsCloudPlaceholderAttributes(CoreUnderTest.FileAttributeRecallOnOpen),
            "corner: recall-on-open is cloud placeholder");
        Check(CoreUnderTest.IsCloudPlaceholderAttributes(
            CoreUnderTest.FileAttributeOffline | 0x20),
            "corner: offline combined with archive");
        Check(!CoreUnderTest.IsCloudPlaceholderAttributes(0x20),
            "corner: archive is not cloud placeholder");
        Check(!CoreUnderTest.IsCloudPlaceholderAttributes(0),
            "corner: normal file is not cloud placeholder");

        // 性能瓶颈判断：样本量、CPU占用阈值与越界钳制。
        CheckEqual("样本小", CoreUnderTest.ClassifyPerformanceBottleneck(10, 1.0, 0.9),
            "perf: small count");
        CheckEqual("样本小", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 0.01, 0.9),
            "perf: short duration");
        CheckEqual("CPU", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 1.0, 0.9),
            "perf: cpu bound");
        CheckEqual("I/O", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 1.0, 0.1),
            "perf: io bound");
        CheckEqual("CPU/I/O混合", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 1.0, 0.5),
            "perf: mixed");
        CheckEqual("I/O", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 1.0, -1),
            "perf: negative cpu clamps to io");
        CheckEqual("CPU", CoreUnderTest.ClassifyPerformanceBottleneck(1000, 1.0, 2),
            "perf: over-range cpu clamps to cpu");

        // 同卷判断：盘符大小写不敏感，不同盘符不同卷。
        Check(CoreUnderTest.IsSameVolumePath(@"C:\a\1.txt", @"c:\b\2.txt"),
            "corner: same drive case-insensitive");
        Check(!CoreUnderTest.IsSameVolumePath(@"C:\a\1.txt", @"D:\b\2.txt"),
            "corner: different drives");
        Check(CoreUnderTest.IsSameVolumePath(@"\\server\share\a\1.txt", @"\\SERVER\SHARE\b\2.txt"),
            "corner: same UNC share case-insensitive");
        Check(!CoreUnderTest.IsSameVolumePath(@"\\server\share\1.txt", @"\\server\other\2.txt"),
            "corner: different UNC shares");
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TestStandardHashes();
        TestBlake3();
        TestHashStreamBoundaries();
        TestFilenameDate();
        TestChecksumBadge();
        TestTemplateAndPath();
        TestDateResolution();
        TestFilter();
        TestTransferEngine();
        TestPreset();
        TestPresetJsonState();
        TestFlatPresetStore();
        TestPresetStructureCompatibility();
        TestExtensionPresetStore();
        TestCornerCases();
        Console.WriteLine();
        Console.WriteLine("RESULT passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }
}
