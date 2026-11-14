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
        Check(!CoreUnderTest.TryExtractFilenameDate("2020-02-021.jpg", 2000, out date),
            "filename date: reject trailing digit");
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
            CoreUnderTest.BuildChecksumBadge("CRC32", 8, "", "", "7f7f7f7f"), "badge: CRC32 full");
        CheckEqual("[CRC32_4-7f7f]",
            CoreUnderTest.BuildChecksumBadge("CRC32", 4, "", "", "7f7f7f7f"), "badge: CRC32 truncated");
        CheckEqual("[CHK:CRC32_4-7f7f:END]",
            CoreUnderTest.BuildChecksumBadge("CRC32", 4, "CHK:", ":END", "7f7f7f7f"), "badge: prefix/suffix");
        CheckEqual("[SHA-256-" + new string('a', 64) + "]",
            CoreUnderTest.BuildChecksumBadge("SHA256", 64, "", "", new string('a', 64)), "badge: SHA-256");
        CheckEqual("[MD5-" + new string('b', 32) + "]",
            CoreUnderTest.BuildChecksumBadge("md5", 32, "", "", new string('b', 32)), "badge: MD5 lowercase input");
        CheckEqual("[BLAKE3-" + new string('c', 64) + "]",
            CoreUnderTest.BuildChecksumBadge("BLAKE3", 64, "", "", new string('c', 64)), "badge: BLAKE3");
        CheckEqual("7f7f", CoreUnderTest.TruncateHash("7f7f7f7f", 4), "truncate: head");
        CheckEqual("[CRC32-…]",
            CoreUnderTest.BuildPendingChecksumBadge("CRC32", 8, "", ""), "badge: pending");
    }

    private static void TestTemplateAndPath()
    {
        string target;
        string error;
        string template = "【根路径】/【日期yyyy】.【日期mm】/aaa/bbb/【源文件名】-【校验码】.【扩展名】";
        bool ok = CoreUnderTest.TryBuildTargetPath(template, @"C:\A\B\C",
            new DateTime(2020, 6, 15), "D", "TXT", "[CRC32_4-7f7f]", out target, out error);
        Check(ok, "path: example builds");
        CheckEqual(@"C:\A\B\C\2020.06\aaa\bbb\D-[CRC32_4-7f7f].TXT", target, "path: example result");
        if (!ok) Console.WriteLine("      error: " + error);

        ok = CoreUnderTest.TryBuildTargetPath("【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: relative template gets root");

        ok = CoreUnderTest.TryBuildTargetPath("D:/Out/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), "D", "TXT", "", out target, out error);
        Check(ok && target == @"D:\Out\D.TXT", "path: absolute template ignores root");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.txt", @"C:\A",
            new DateTime(2020, 6, 15), "【日期yyyy】", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\【日期yyyy】.txt", "path: token in value is not recursively replaced");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【未知】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), "D", "TXT", "", out target, out error);
        Check(!ok && error != null && error.IndexOf("未知胶囊") >= 0, "path: unknown token rejected");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/../D.TXT", @"C:\A",
            new DateTime(2020, 6, 15), "D", "TXT", "", out target, out error);
        Check(!ok, "path: parent segment rejected");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), "D", "", "", out target, out error);
        Check(!ok, "path: empty extension leaves trailing dot and fails");

        ok = CoreUnderTest.TryBuildTargetPath("【根路径】/【源文件名】.【扩展名】", @"C:\A",
            new DateTime(2020, 6, 15), "D", "TXT", "", out target, out error);
        Check(ok && target == @"C:\A\D.TXT", "path: simple template");

        string normalized = CoreUnderTest.NormalizeTargetPath("C://A/B//C", "", out error);
        CheckEqual(@"C:\A\B\C", normalized, "path: collapse duplicate separators");
        normalized = CoreUnderTest.NormalizeTargetPath(@"\\server\share\\folder", "", out error);
        CheckEqual(@"\\server\share\folder", normalized, "path: preserve UNC prefix");
        normalized = CoreUnderTest.NormalizeTargetPath("B/C.TXT", @"C:\A", out error);
        CheckEqual(@"C:\A\B\C.TXT", normalized, "path: combine root");

        Check(CoreUnderTest.ValidateWindowsPath(@"C:\A\B.TXT", out error), "validate: normal drive path");
        Check(CoreUnderTest.ValidateWindowsPath(@"\\server\share\B.TXT", out error), "validate: UNC path");
        Check(!CoreUnderTest.ValidateWindowsPath(@"\\server\share", out error), "validate: UNC without file");
        Check(!CoreUnderTest.ValidateWindowsPath(@"C:\A\..\B.TXT", out error), "validate: dot-dot");
        Check(!CoreUnderTest.ValidateWindowsPath(@"C:\A\B*.TXT", out error), "validate: wildcard");
        Check(!CoreUnderTest.ValidateWindowsPath(@"C:A\B.TXT", out error), "validate: drive-relative");
        Check(!CoreUnderTest.ValidateWindowsPath(@"C:\A\B.", out error), "validate: trailing dot");
        Check(!CoreUnderTest.ValidateWindowsPath(@"C:\A\B ", out error), "validate: trailing space");
        Check(CoreUnderTest.ValidateWindowsPath(@"C:\A\B", out error), "validate: path without extension is valid");
    }

    private static void TestPreset()
    {
        Dictionary<string, string> config = CoreUnderTest.CreateDefaultConfig(2000);
        config["rootMode"] = "custom";
        config["rootPath"] = @"D:\Out";
        config["startYear"] = "2010";
        config["dateSource"] = "exif";
        config["checksumAlgorithm"] = "SHA-256";
        config["checksumDigits"] = "16";
        config["checksumPrefix"] = "P";
        config["checksumSuffix"] = "S";
        config["template"] = "【根路径】/X-【校验码】.【扩展名】";

        Dictionary<string, object> preset = CoreUnderTest.BuildPresetDictionary(config);
        Check(Convert.ToString(preset["presetVersion"]) == "0.01", "preset: version");
        Check(Convert.ToString(preset["rootMode"]) == "custom", "preset: root mode");
        Check(Convert.ToString(preset["checksumDigits"]) == "16", "preset: digits");

        Dictionary<string, string> loaded = CoreUnderTest.CreateDefaultConfig(2000);
        bool applied = CoreUnderTest.ApplyPresetDictionary(preset, loaded);
        Check(applied, "preset: applied");
        CheckEqual(@"D:\Out", loaded["rootPath"], "preset: root path");
        CheckEqual("2010", loaded["startYear"], "preset: start year");
        CheckEqual("exif", loaded["dateSource"], "preset: date source");
        CheckEqual("SHA-256", loaded["checksumAlgorithm"], "preset: algorithm");
        CheckEqual("16", loaded["checksumDigits"], "preset: digits roundtrip");
        CheckEqual("P", loaded["checksumPrefix"], "preset: prefix");
        CheckEqual("S", loaded["checksumSuffix"], "preset: suffix");
        CheckEqual("【根路径】/X-【校验码】.【扩展名】", loaded["template"], "preset: template");

        Dictionary<string, string> partial = CoreUnderTest.CreateDefaultConfig(2000);
        Dictionary<string, object> p2 = new Dictionary<string, object>();
        p2["checksumAlgorithm"] = "CRC32";
        p2["checksumDigits"] = "99";
        Check(CoreUnderTest.ApplyPresetDictionary(p2, partial), "preset: partial applied");
        CheckEqual("8", partial["checksumDigits"], "preset: digits clamped to algorithm max");

        Dictionary<string, string> aliases = CoreUnderTest.CreateDefaultConfig(2000);
        Dictionary<string, object> p3 = new Dictionary<string, object>();
        p3["dateSource"] = "修改日期";
        p3["checksumAlgorithm"] = "sha256";
        p3["rootMode"] = "其他文件夹";
        Check(CoreUnderTest.ApplyPresetDictionary(p3, aliases), "preset: aliases applied");
        CheckEqual("modified", aliases["dateSource"], "preset: Chinese date alias");
        CheckEqual("SHA-256", aliases["checksumAlgorithm"], "preset: algorithm alias");
        CheckEqual("custom", aliases["rootMode"], "preset: root alias");
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        TestStandardHashes();
        TestBlake3();
        TestFilenameDate();
        TestChecksumBadge();
        TestTemplateAndPath();
        TestPreset();
        Console.WriteLine();
        Console.WriteLine("RESULT passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }
}
