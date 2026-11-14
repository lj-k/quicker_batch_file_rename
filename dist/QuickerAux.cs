// 批量重命名器 - Quicker 辅助C#代码（由 tools/build_quicker_code.py 生成）
// 版本：0.01
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Quicker.Public;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using DialogResult = System.Windows.Forms.DialogResult;
using FolderBrowserDialog = System.Windows.Forms.FolderBrowserDialog;

// ============================================================================
// 批量重命名器核心逻辑（Quicker 辅助 C# 代码片段）
// 版本：0.01
// 说明：本文件不包含 namespace / class 声明，部署时会与 CustomWindow.Aux.cs
//       合并后放入 Quicker【自定义窗口】的【辅助C#代码】中。
// 兼容性：按 C# 5.0 / .NET Framework 4.x 语法编写。
// ============================================================================

public const string DefaultTemplate = "【根路径】/【日期yyyy】.【日期mm】/【源文件名】-【校验码】.【扩展名】";

public static readonly string[] SupportedAlgorithms = new string[] { "CRC32", "BLAKE3", "MD5", "SHA-256" };
public static readonly string[] SupportedDateSources = new string[] { "filename", "exif", "modified", "created" };
public static readonly string[] TemplateTokens = new string[] {
    "【日期yyyy】", "【日期mm】", "【日期dd】", "【根路径】",
    "【源文件名】", "【扩展名】", "【校验码】"
};

private static readonly Regex FilenameDate1Regex =
    new Regex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
private static readonly Regex FilenameDate2Regex =
    new Regex(@"(?<!\d)(\d{8})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
private static readonly Regex TemplateTokenRegex =
    new Regex("【[^】]*】", RegexOptions.Compiled | RegexOptions.CultureInvariant);

private static readonly uint[] Blake3Iv = new uint[] {
    0x6A09E667U, 0xBB67AE85U, 0x3C6EF372U, 0xA54FF53AU,
    0x510E527FU, 0x9B05688CU, 0x1F83D9ABU, 0x5BE0CD19U
};

private static readonly int[] Blake3Permutation = new int[] {
    2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8
};

private const uint Blake3ChunkStart = 1U;
private const uint Blake3ChunkEnd = 2U;
private const uint Blake3Parent = 4U;
private const uint Blake3Root = 8U;

private static uint[] _crc32Table = null;
private static readonly object _crc32TableLock = new object();

// ---------------------------------------------------------------------------
// 基础规范化
// ---------------------------------------------------------------------------

public static string NormalizeAlgorithm(string algorithm)
{
    string value = (algorithm == null ? "" : algorithm.Trim()).ToUpperInvariant();
    if (value == "SHA256" || value == "SHA_256" || value == "SHA-256")
        return "SHA-256";
    if (value == "BLAKE-3" || value == "BLAKE_3")
        return "BLAKE3";
    if (value == "CRC-32" || value == "CRC_32")
        return "CRC32";
    return value;
}

public static bool IsSupportedAlgorithm(string algorithm)
{
    string value = NormalizeAlgorithm(algorithm);
    for (int i = 0; i < SupportedAlgorithms.Length; i++)
    {
        if (SupportedAlgorithms[i] == value)
            return true;
    }
    return false;
}

public static int GetAlgorithmMaxDigits(string algorithm)
{
    string value = NormalizeAlgorithm(algorithm);
    if (value == "CRC32") return 8;
    if (value == "BLAKE3") return 64;
    if (value == "MD5") return 32;
    if (value == "SHA-256") return 64;
    return 0;
}

public static string NormalizeDateSource(string source)
{
    string value = (source == null ? "" : source.Trim()).ToLowerInvariant();
    if (value == "filename" || value == "file" || value == "文件名" || value == "文件名中的日期")
        return "filename";
    if (value == "exif" || value == "exif拍摄日期" || value == "拍摄日期")
        return "exif";
    if (value == "modified" || value == "修改日期")
        return "modified";
    if (value == "created" || value == "创建日期")
        return "created";
    return "filename";
}

public static string NormalizeRootMode(string mode)
{
    string value = (mode == null ? "" : mode.Trim()).ToLowerInvariant();
    if (value == "custom" || value == "其他文件夹")
        return "custom";
    return "current";
}

public static int NormalizeStartYear(int startYear)
{
    int currentYear = DateTime.Now.Year;
    if (startYear <= 0)
        return 2000;
    if (startYear < 1900)
        return 1900;
    if (startYear > currentYear)
        return currentYear;
    return startYear;
}

// ---------------------------------------------------------------------------
// 文件名日期提取
// ---------------------------------------------------------------------------

public static bool TryExtractFilenameDate(string fileName, int startYear, out DateTime result)
{
    result = DateTime.MinValue;
    if (fileName == null || fileName.Length == 0)
        return false;

    int minYear = NormalizeStartYear(startYear);
    int maxYear = DateTime.Now.Year;
    string baseName = fileName;
    try
    {
        baseName = Path.GetFileNameWithoutExtension(fileName);
    }
    catch
    {
        baseName = fileName;
    }
    if (baseName == null || baseName.Length == 0)
        return false;

    MatchCollection matches = FilenameDate1Regex.Matches(baseName);
    for (int i = 0; i < matches.Count; i++)
    {
        Match match = matches[i];
        int year;
        int month;
        int day;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out year))
            continue;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out month))
            continue;
        if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out day))
            continue;
        if (IsValidDate(year, month, day, minYear, maxYear))
        {
            result = new DateTime(year, month, day);
            return true;
        }
    }

    matches = FilenameDate2Regex.Matches(baseName);
    for (int i = 0; i < matches.Count; i++)
    {
        string digits = matches[i].Groups[1].Value;
        int year = int.Parse(digits.Substring(0, 4), CultureInfo.InvariantCulture);
        int month = int.Parse(digits.Substring(4, 2), CultureInfo.InvariantCulture);
        int day = int.Parse(digits.Substring(6, 2), CultureInfo.InvariantCulture);
        if (IsValidDate(year, month, day, minYear, maxYear))
        {
            result = new DateTime(year, month, day);
            return true;
        }
    }

    return false;
}

private static bool IsValidDate(int year, int month, int day, int minYear, int maxYear)
{
    if (year < minYear || year > maxYear)
        return false;
    if (month < 1 || month > 12)
        return false;
    if (day < 1 || day > DateTime.DaysInMonth(year, month))
        return false;
    return true;
}

public static string GetDatePart(DateTime date, string token)
{
    if (token == "【日期yyyy】")
        return date.ToString("yyyy", CultureInfo.InvariantCulture);
    if (token == "【日期mm】")
        return date.ToString("MM", CultureInfo.InvariantCulture);
    if (token == "【日期dd】")
        return date.ToString("dd", CultureInfo.InvariantCulture);
    return "";
}

// ---------------------------------------------------------------------------
// 校验码
// ---------------------------------------------------------------------------

public static string GetAlgorithmLabel(string algorithm, int digits)
{
    string value = NormalizeAlgorithm(algorithm);
    int maxDigits = GetAlgorithmMaxDigits(value);
    int actualDigits = digits;
    if (maxDigits <= 0) maxDigits = 1;
    if (actualDigits < 1) actualDigits = 1;
    if (actualDigits > maxDigits) actualDigits = maxDigits;
    if (actualDigits == maxDigits)
        return value;
    return value + "_" + actualDigits.ToString(CultureInfo.InvariantCulture);
}

public static string TruncateHash(string hex, int digits)
{
    if (hex == null) return "";
    int maxDigits = hex.Length;
    int actual = digits;
    if (actual < 1) actual = 1;
    if (actual > maxDigits) actual = maxDigits;
    return hex.Substring(0, actual);
}

public static string BuildChecksumBadge(string algorithm, int digits, string prefix, string suffix, string hex)
{
    string value = NormalizeAlgorithm(algorithm);
    int maxDigits = GetAlgorithmMaxDigits(value);
    int actualDigits = digits;
    if (maxDigits <= 0) maxDigits = 1;
    if (actualDigits < 1) actualDigits = 1;
    if (actualDigits > maxDigits) actualDigits = maxDigits;

    string label = GetAlgorithmLabel(value, actualDigits);
    string code = TruncateHash(hex, actualDigits);
    string left = (prefix == null ? "" : prefix);
    string right = (suffix == null ? "" : suffix);
    return "[" + left + label + "-" + code + right + "]";
}

public static string BuildPendingChecksumBadge(string algorithm, int digits, string prefix, string suffix)
{
    string value = NormalizeAlgorithm(algorithm);
    int maxDigits = GetAlgorithmMaxDigits(value);
    int actualDigits = digits;
    if (maxDigits <= 0) maxDigits = 1;
    if (actualDigits < 1) actualDigits = 1;
    if (actualDigits > maxDigits) actualDigits = maxDigits;

    string label = GetAlgorithmLabel(value, actualDigits);
    string left = (prefix == null ? "" : prefix);
    string right = (suffix == null ? "" : suffix);
    return "[" + left + label + "-…" + right + "]";
}

public static string ComputeHashHex(string filePath, string algorithm, out string error)
{
    error = null;
    string value = NormalizeAlgorithm(algorithm);
    if (!IsSupportedAlgorithm(value))
    {
        error = "不支持的校验算法：" + (algorithm == null ? "" : algorithm);
        return null;
    }

    try
    {
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (value == "CRC32")
                return ComputeCrc32Hex(stream);
            if (value == "BLAKE3")
                return ComputeBlake3Hex(stream);

            using (HashAlgorithm hashAlgorithm = value == "MD5" ? (HashAlgorithm)MD5.Create() : (HashAlgorithm)SHA256.Create())
            {
                byte[] hash = hashAlgorithm.ComputeHash(stream);
                return ToHex(hash);
            }
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
}

public static string ComputeHashHexFromBytes(byte[] data, string algorithm, out string error)
{
    error = null;
    try
    {
        using (MemoryStream stream = new MemoryStream(data == null ? new byte[0] : data))
        {
            string value = NormalizeAlgorithm(algorithm);
            if (value == "CRC32")
                return ComputeCrc32Hex(stream);
            if (value == "BLAKE3")
                return ComputeBlake3Hex(stream);
            if (value == "MD5")
            {
                using (MD5 md5 = MD5.Create())
                    return ToHex(md5.ComputeHash(stream));
            }
            if (value == "SHA-256")
            {
                using (SHA256 sha = SHA256.Create())
                    return ToHex(sha.ComputeHash(stream));
            }
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
    error = "不支持的校验算法";
    return null;
}

private static string ComputeCrc32Hex(Stream stream)
{
    EnsureCrc32Table();
    uint crc = 0xFFFFFFFFU;
    byte[] buffer = new byte[81920];
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (int i = 0; i < read; i++)
        {
            crc = (crc >> 8) ^ _crc32Table[(crc ^ buffer[i]) & 0xFFU];
        }
    }
    crc = ~crc;
    return crc.ToString("x8", CultureInfo.InvariantCulture);
}

private static void EnsureCrc32Table()
{
    if (_crc32Table != null)
        return;
    lock (_crc32TableLock)
    {
        if (_crc32Table != null)
            return;
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++)
            {
                if ((value & 1U) != 0U)
                    value = 0xEDB88320U ^ (value >> 1);
                else
                    value = value >> 1;
            }
            table[i] = value;
        }
        _crc32Table = table;
    }
}

private static string ToHex(byte[] data)
{
    if (data == null) return "";
    char[] chars = new char[data.Length * 2];
    const string hexChars = "0123456789abcdef";
    for (int i = 0; i < data.Length; i++)
    {
        chars[i * 2] = hexChars[(data[i] >> 4) & 0xF];
        chars[i * 2 + 1] = hexChars[data[i] & 0xF];
    }
    return new string(chars);
}

// ---------------------------------------------------------------------------
// BLAKE3（默认 32 字节输出，无密钥模式）
// ---------------------------------------------------------------------------

private static string ComputeBlake3Hex(Stream stream)
{
    byte[] chunk = new byte[1024];
    int chunkLength = 0;
    ulong chunkCounter = 0;
    List<uint[]> stack = new List<uint[]>();
    byte[] readBuffer = new byte[81920];
    int read;

    while ((read = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
    {
        int position = 0;
        while (position < read)
        {
            if (chunkLength == 1024)
            {
                uint[] chunkCv = Blake3ChunkCv(chunk, 1024, chunkCounter);
                chunkCounter++;
                Blake3AddChunkCv(stack, chunkCv, chunkCounter);
                chunkLength = 0;
            }

            int copy = Math.Min(1024 - chunkLength, read - position);
            Array.Copy(readBuffer, position, chunk, chunkLength, copy);
            chunkLength += copy;
            position += copy;
        }
    }

    uint[] inputCv;
    uint[] blockWords;
    uint blockLength;
    uint flags;
    Blake3ChunkOutput(chunk, chunkLength, chunkCounter, out inputCv, out blockWords, out blockLength, out flags);

    ulong outputCounter = chunkCounter;
    while (stack.Count > 0)
    {
        uint[] compressed = Blake3Compress(inputCv, blockWords, outputCounter, blockLength, flags);
        uint[] rightCv = new uint[8];
        Array.Copy(compressed, rightCv, 8);

        uint[] leftCv = stack[stack.Count - 1];
        stack.RemoveAt(stack.Count - 1);

        uint[] parentBlock = new uint[16];
        Array.Copy(leftCv, 0, parentBlock, 0, 8);
        Array.Copy(rightCv, 0, parentBlock, 8, 8);

        inputCv = (uint[])Blake3Iv.Clone();
        blockWords = parentBlock;
        blockLength = 64;
        flags = Blake3Parent;
        outputCounter = 0;
    }

    uint[] root = Blake3Compress(inputCv, blockWords, 0, blockLength, flags | Blake3Root);
    byte[] output = new byte[32];
    for (int i = 0; i < 8; i++)
    {
        uint word = root[i];
        output[i * 4] = (byte)(word & 0xFFU);
        output[i * 4 + 1] = (byte)((word >> 8) & 0xFFU);
        output[i * 4 + 2] = (byte)((word >> 16) & 0xFFU);
        output[i * 4 + 3] = (byte)((word >> 24) & 0xFFU);
    }
    return ToHex(output);
}

private static uint[] Blake3ChunkCv(byte[] chunk, int length, ulong chunkCounter)
{
    uint[] inputCv;
    uint[] blockWords;
    uint blockLength;
    uint flags;
    Blake3ChunkOutput(chunk, length, chunkCounter, out inputCv, out blockWords, out blockLength, out flags);
    uint[] compressed = Blake3Compress(inputCv, blockWords, chunkCounter, blockLength, flags);
    uint[] cv = new uint[8];
    Array.Copy(compressed, cv, 8);
    return cv;
}

private static void Blake3ChunkOutput(byte[] chunk, int length, ulong chunkCounter,
    out uint[] inputCv, out uint[] blockWords, out uint blockLength, out uint flags)
{
    uint[] cv = (uint[])Blake3Iv.Clone();
    int offset = 0;
    while (true)
    {
        int remaining = length - offset;
        if (remaining > 64) remaining = 64;
        if (remaining < 0) remaining = 0;

        uint[] block = new uint[16];
        for (int i = 0; i < remaining; i++)
        {
            block[i / 4] |= (uint)chunk[offset + i] << ((i % 4) * 8);
        }

        uint blockFlags = (offset == 0 ? Blake3ChunkStart : 0U);
        bool isLast = (offset + remaining >= length);
        if (isLast)
        {
            blockFlags |= Blake3ChunkEnd;
            inputCv = cv;
            blockWords = block;
            blockLength = (uint)remaining;
            flags = blockFlags;
            return;
        }

        uint[] compressed = Blake3Compress(cv, block, chunkCounter, (uint)remaining, blockFlags);
        uint[] nextCv = new uint[8];
        Array.Copy(compressed, nextCv, 8);
        cv = nextCv;
        offset += remaining;
    }
}

private static void Blake3AddChunkCv(List<uint[]> stack, uint[] cv, ulong totalChunks)
{
    while ((totalChunks & 1UL) == 0UL)
    {
        uint[] left = stack[stack.Count - 1];
        stack.RemoveAt(stack.Count - 1);
        cv = Blake3ParentCv(left, cv);
        totalChunks >>= 1;
    }
    stack.Add(cv);
}

private static uint[] Blake3ParentCv(uint[] left, uint[] right)
{
    uint[] block = new uint[16];
    Array.Copy(left, 0, block, 0, 8);
    Array.Copy(right, 0, block, 8, 8);
    uint[] compressed = Blake3Compress(Blake3Iv, block, 0, 64, Blake3Parent);
    uint[] cv = new uint[8];
    Array.Copy(compressed, cv, 8);
    return cv;
}

private static uint[] Blake3Compress(uint[] cv, uint[] block, ulong counter, uint blockLength, uint flags)
{
    uint[] state = new uint[16];
    for (int i = 0; i < 8; i++)
        state[i] = cv[i];
    state[8] = Blake3Iv[0];
    state[9] = Blake3Iv[1];
    state[10] = Blake3Iv[2];
    state[11] = Blake3Iv[3];
    state[12] = (uint)(counter & 0xFFFFFFFFUL);
    state[13] = (uint)(counter >> 32);
    state[14] = blockLength;
    state[15] = flags;

    uint[] message = new uint[16];
    Array.Copy(block, message, 16);

    for (int round = 0; round < 7; round++)
    {
        Blake3G(state, 0, 4, 8, 12, message[0], message[1]);
        Blake3G(state, 1, 5, 9, 13, message[2], message[3]);
        Blake3G(state, 2, 6, 10, 14, message[4], message[5]);
        Blake3G(state, 3, 7, 11, 15, message[6], message[7]);
        Blake3G(state, 0, 5, 10, 15, message[8], message[9]);
        Blake3G(state, 1, 6, 11, 12, message[10], message[11]);
        Blake3G(state, 2, 7, 8, 13, message[12], message[13]);
        Blake3G(state, 3, 4, 9, 14, message[14], message[15]);

        if (round != 6)
        {
            uint[] permuted = new uint[16];
            for (int i = 0; i < 16; i++)
                permuted[i] = message[Blake3Permutation[i]];
            message = permuted;
        }
    }

    uint[] output = new uint[16];
    for (int i = 0; i < 8; i++)
    {
        output[i] = state[i] ^ state[i + 8];
        output[i + 8] = state[i + 8] ^ cv[i];
    }
    return output;
}

private static void Blake3G(uint[] state, int a, int b, int c, int d, uint mx, uint my)
{
    state[a] = state[a] + state[b] + mx;
    state[d] = RotateRight(state[d] ^ state[a], 16);
    state[c] = state[c] + state[d];
    state[b] = RotateRight(state[b] ^ state[c], 12);
    state[a] = state[a] + state[b] + my;
    state[d] = RotateRight(state[d] ^ state[a], 8);
    state[c] = state[c] + state[d];
    state[b] = RotateRight(state[b] ^ state[c], 7);
}

private static uint RotateRight(uint value, int count)
{
    return (value >> count) | (value << (32 - count));
}

// ---------------------------------------------------------------------------
// 模板与路径
// ---------------------------------------------------------------------------

public static bool IsKnownToken(string token)
{
    if (token == null) return false;
    for (int i = 0; i < TemplateTokens.Length; i++)
    {
        if (TemplateTokens[i] == token)
            return true;
    }
    return false;
}

public static bool ValidateTemplateTokens(string template, out string badToken)
{
    badToken = null;
    if (template == null || template.Length == 0)
    {
        badToken = "";
        return false;
    }

    MatchCollection matches = TemplateTokenRegex.Matches(template);
    for (int i = 0; i < matches.Count; i++)
    {
        if (!IsKnownToken(matches[i].Value))
        {
            badToken = matches[i].Value;
            return false;
        }
    }
    return true;
}

public static bool TemplateContainsToken(string template, string token)
{
    return template != null && token != null && template.IndexOf(token, StringComparison.Ordinal) >= 0;
}

public static string RenderTemplate(string template, IDictionary<string, string> values)
{
    string text = template == null ? "" : template;
    StringBuilder builder = new StringBuilder(text.Length + 32);
    int index = 0;
    while (index < text.Length)
    {
        if (text[index] == '【')
        {
            int end = text.IndexOf('】', index + 1);
            if (end > index)
            {
                string token = text.Substring(index, end - index + 1);
                string value;
                if (values != null && values.TryGetValue(token, out value))
                {
                    builder.Append(value);
                    index = end + 1;
                    continue;
                }
            }
        }
        builder.Append(text[index]);
        index++;
    }
    return builder.ToString();
}

public static bool TryBuildTargetPath(string template, string rootDirectory, DateTime effectiveDate,
    string baseName, string extension, string checksumBadge, out string targetPath, out string error)
{
    targetPath = null;
    error = null;

    if (template == null || template.Trim().Length == 0)
    {
        error = "目标路径模板为空";
        return false;
    }

    string badToken;
    if (!ValidateTemplateTokens(template, out badToken))
    {
        if (badToken == null || badToken.Length == 0)
            error = "目标路径模板为空";
        else
            error = "模板包含未知胶囊：" + badToken;
        return false;
    }

    Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    values["【日期yyyy】"] = effectiveDate.ToString("yyyy", CultureInfo.InvariantCulture);
    values["【日期mm】"] = effectiveDate.ToString("MM", CultureInfo.InvariantCulture);
    values["【日期dd】"] = effectiveDate.ToString("dd", CultureInfo.InvariantCulture);
    values["【根路径】"] = rootDirectory == null ? "" : rootDirectory;
    values["【源文件名】"] = baseName == null ? "" : baseName;
    values["【扩展名】"] = extension == null ? "" : extension;
    values["【校验码】"] = checksumBadge == null ? "" : checksumBadge;

    string rendered = RenderTemplate(template, values);
    string normalized = NormalizeTargetPath(rendered, rootDirectory, out error);
    if (normalized == null)
        return false;

    if (!ValidateWindowsPath(normalized, out error))
        return false;

    targetPath = normalized;
    return true;
}

public static string NormalizeTargetPath(string renderedPath, string rootDirectory, out string error)
{
    error = null;
    if (renderedPath == null)
    {
        error = "目标路径为空";
        return null;
    }

    string path = renderedPath.Replace('/', '\\');
    path = CollapseBackslashes(path);

    bool rooted = IsRootedWindowsPath(path);
    if (!rooted)
    {
        if (rootDirectory == null || rootDirectory.Trim().Length == 0)
        {
            error = "模板未包含【根路径】且根路径为空";
            return null;
        }
        string root = CollapseBackslashes(rootDirectory.Replace('/', '\\')).TrimEnd('\\');
        path = root + "\\" + path.TrimStart('\\');
        path = CollapseBackslashes(path);
    }

    return path;
}

private static string CollapseBackslashes(string path)
{
    StringBuilder builder = new StringBuilder(path.Length);
    for (int i = 0; i < path.Length; i++)
    {
        char c = path[i];
        if (c == '\\' && builder.Length > 0 && builder[builder.Length - 1] == '\\')
        {
            bool isUncPrefix = (builder.Length == 1 && path.Length > 1 && path[1] == '\\');
            if (!isUncPrefix)
                continue;
        }
        builder.Append(c);
    }
    return builder.ToString();
}

public static bool IsRootedWindowsPath(string path)
{
    if (path == null || path.Length < 2)
        return false;
    if (char.IsLetter(path[0]) && path[1] == ':')
        return true;
    if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
        return true;
    return false;
}

public static bool ValidateWindowsPath(string path, out string error)
{
    error = null;
    if (path == null || path.Trim().Length == 0)
    {
        error = "目标路径为空";
        return false;
    }
    if (path.IndexOf('/') >= 0)
    {
        error = "目标路径包含未规范化的斜杠";
        return false;
    }
    if (path[path.Length - 1] == '\\')
    {
        error = "目标路径以目录分隔符结尾，缺少文件名";
        return false;
    }

    bool isUnc = path.StartsWith("\\\\", StringComparison.Ordinal);
    bool isDrive = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    if (!isUnc && !isDrive)
    {
        error = "目标路径不是绝对路径（应为 X:\\... 或 \\\\服务器\\共享\\...）";
        return false;
    }

    string[] segments = path.Split('\\');
    if (isUnc)
    {
        int nonEmptySegments = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0)
                nonEmptySegments++;
        }
        if (nonEmptySegments < 3)
        {
            error = "UNC 目标路径至少需要服务器、共享和文件名";
            return false;
        }
    }
    for (int i = 0; i < segments.Length; i++)
    {
        string segment = segments[i];
        if (segment.Length == 0)
            continue;
        if (i == 0 && isDrive && segment.Length == 2 && segment[1] == ':')
            continue;

        if (segment == "." || segment == "..")
        {
            error = "目标路径不能包含 . 或 .. 目录段";
            return false;
        }
        if (segment[segment.Length - 1] == ' ' || segment[segment.Length - 1] == '.')
        {
            error = "路径段不能以空格或点结尾：" + segment;
            return false;
        }
        for (int j = 0; j < segment.Length; j++)
        {
            char c = segment[j];
            if (c < 32 || c == '<' || c == '>' || c == ':' || c == '"' || c == '|' || c == '?' || c == '*')
            {
                error = "路径段包含非法字符：" + segment;
                return false;
            }
        }
    }

    string fileName = GetLastPathSegment(path);
    if (fileName == null || fileName.Length == 0)
    {
        error = "目标路径缺少文件名";
        return false;
    }
    return true;
}

public static string GetLastPathSegment(string path)
{
    if (path == null) return "";
    int end = path.Length - 1;
    while (end >= 0 && path[end] == '\\')
        end--;
    if (end < 0) return "";
    int start = end;
    while (start >= 0 && path[start] != '\\')
        start--;
    return path.Substring(start + 1, end - start);
}

// ---------------------------------------------------------------------------
// 配置与预设
// ---------------------------------------------------------------------------

public static Dictionary<string, string> CreateDefaultConfig(int startYear)
{
    Dictionary<string, string> config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    config["rootMode"] = "current";
    config["rootPath"] = "";
    config["startYear"] = NormalizeStartYear(startYear).ToString(CultureInfo.InvariantCulture);
    config["dateSource"] = "filename";
    config["checksumAlgorithm"] = "CRC32";
    config["checksumDigits"] = "8";
    config["checksumPrefix"] = "";
    config["checksumSuffix"] = "";
    config["template"] = DefaultTemplate;
    return config;
}

public static int GetConfigInt(Dictionary<string, string> config, string key, int defaultValue)
{
    if (config == null || key == null)
        return defaultValue;
    string value;
    if (!config.TryGetValue(key, out value) || value == null)
        return defaultValue;
    int result;
    if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        return defaultValue;
    return result;
}

public static string GetConfigString(Dictionary<string, string> config, string key, string defaultValue)
{
    if (config == null || key == null)
        return defaultValue;
    string value;
    if (!config.TryGetValue(key, out value) || value == null)
        return defaultValue;
    return value;
}

public static void NormalizeConfig(Dictionary<string, string> config)
{
    if (config == null)
        return;

    string rootMode = NormalizeRootMode(GetConfigString(config, "rootMode", "current"));
    config["rootMode"] = rootMode;
    config["rootPath"] = GetConfigString(config, "rootPath", "");

    int startYear;
    if (!int.TryParse(GetConfigString(config, "startYear", "2000"), NumberStyles.Integer, CultureInfo.InvariantCulture, out startYear))
        startYear = 2000;
    config["startYear"] = NormalizeStartYear(startYear).ToString(CultureInfo.InvariantCulture);

    config["dateSource"] = NormalizeDateSource(GetConfigString(config, "dateSource", "filename"));

    string algorithm = NormalizeAlgorithm(GetConfigString(config, "checksumAlgorithm", "CRC32"));
    if (!IsSupportedAlgorithm(algorithm))
        algorithm = "CRC32";
    config["checksumAlgorithm"] = algorithm;

    int digits;
    if (!int.TryParse(GetConfigString(config, "checksumDigits", "8"), NumberStyles.Integer, CultureInfo.InvariantCulture, out digits))
        digits = GetAlgorithmMaxDigits(algorithm);
    int maxDigits = GetAlgorithmMaxDigits(algorithm);
    if (digits < 1) digits = 1;
    if (digits > maxDigits) digits = maxDigits;
    config["checksumDigits"] = digits.ToString(CultureInfo.InvariantCulture);

    config["checksumPrefix"] = GetConfigString(config, "checksumPrefix", "");
    config["checksumSuffix"] = GetConfigString(config, "checksumSuffix", "");

    string template = GetConfigString(config, "template", DefaultTemplate);
    if (template == null)
        template = DefaultTemplate;
    config["template"] = template;
}

public static Dictionary<string, object> BuildPresetDictionary(Dictionary<string, string> config)
{
    Dictionary<string, string> normalized = config == null ? CreateDefaultConfig(2000) : config;
    NormalizeConfig(normalized);
    Dictionary<string, object> preset = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    preset["presetVersion"] = "0.01";
    preset["rootMode"] = normalized["rootMode"];
    preset["rootPath"] = normalized["rootPath"];
    preset["startYear"] = normalized["startYear"];
    preset["dateSource"] = normalized["dateSource"];
    preset["checksumAlgorithm"] = normalized["checksumAlgorithm"];
    preset["checksumDigits"] = normalized["checksumDigits"];
    preset["checksumPrefix"] = normalized["checksumPrefix"];
    preset["checksumSuffix"] = normalized["checksumSuffix"];
    preset["template"] = normalized["template"];
    return preset;
}

public static bool ApplyPresetDictionary(IDictionary<string, object> preset, Dictionary<string, string> config)
{
    if (preset == null || config == null)
        return false;

    bool applied = false;
    string[] keys = new string[] {
        "rootMode", "rootPath", "startYear", "dateSource", "checksumAlgorithm",
        "checksumDigits", "checksumPrefix", "checksumSuffix", "template"
    };
    for (int i = 0; i < keys.Length; i++)
    {
        object raw;
        if (!preset.TryGetValue(keys[i], out raw) || raw == null)
            continue;
        config[keys[i]] = Convert.ToString(raw, CultureInfo.InvariantCulture);
        applied = true;
    }
    if (applied)
        NormalizeConfig(config);
    return applied;
}

// ============================================================================
// 批量重命名器界面逻辑（Quicker 自定义窗口辅助 C# 代码）
// 版本：0.01
// 部署时与 RenamerCore.cs 合并，放入【辅助C#代码】。
// 兼容 C# 5.0；三个 Quicker 回调：OnWindowCreated / OnWindowLoaded /
// OnButtonClicked。按钮事件在 OnWindowLoaded 中通过 Click 注册。
// ============================================================================

private static Window _window;
private static IDictionary<string, object> _data;
private static DataGrid _grid;
private static RichTextBox _editor;
private static TextBox _rootPathText;
private static TextBox _digitsText;
private static TextBox _prefixText;
private static TextBox _suffixText;
private static ComboBox _rootModeCombo;
private static ComboBox _startYearCombo;
private static ComboBox _dateSourceCombo;
private static ComboBox _algorithmCombo;
private static TextBlock _checksumBadge;
private static TextBlock _statusText;
private static ProgressBar _progress;
private static Button _browseRootButton;
private static Button _applyButton;

private static ObservableCollection<Dictionary<string, object>> _rows =
    new ObservableCollection<Dictionary<string, object>>();
private static List<Dictionary<string, object>> _files = new List<Dictionary<string, object>>();
private static List<string> _paths = new List<string>();
private static HashSet<string> _pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
private static Dictionary<string, string> _config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
private static Dictionary<string, string> _hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

private static readonly object _hashLock = new object();
private static readonly Regex EditorTokenRegex = new Regex("【[^】]*】", RegexOptions.Compiled);

private static DispatcherTimer _debounceTimer;
private static TextPointer _lastCaret;
private static bool _suppressEvents = false;
private static bool _suppressEditor = false;
private static bool _scanning = false;
private static volatile bool _hashStop = false;
private static volatile bool _hashRunning = false;
private static string _hashAlgorithm = "";
private static volatile int _scanGeneration = 0;
private static volatile int _hashGeneration = 0;
private static volatile int _hashTotal = 0;
private static volatile int _hashDone = 0;
private static int _skippedFolders = 0;
private static int _skippedInvalid = 0;

private static readonly string[] ExifExtensions = new string[] {
    ".jpg", ".jpeg", ".tif", ".tiff", ".png", ".bmp", ".gif", ".heic", ".heif", ".webp"
};

// ---------------------------------------------------------------------------
// Quicker 回调
// ---------------------------------------------------------------------------

public static void OnWindowCreated(Window win, IDictionary<string, object> dataContext,
    ICustomWindowContext winContext)
{
    _window = win;
    _data = dataContext;

    if (dataContext != null)
    {
        if (!dataContext.ContainsKey("files") || dataContext["files"] == null)
            dataContext["files"] = new List<object>();
        if (!dataContext.ContainsKey("preset") || dataContext["preset"] == null)
            dataContext["preset"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        object startYearValue;
        if (!dataContext.TryGetValue("startYear", out startYearValue) || startYearValue == null)
            dataContext["startYear"] = 2000;

        // 取消或直接关闭窗口时，输出为空列表，避免残留上一次的结果。
        dataContext["origPaths"] = new List<object>();
        dataContext["targetPaths"] = new List<object>();
    }
}

public static void OnWindowLoaded(Window win, IDictionary<string, object> dataContext,
    ICustomWindowContext winContext)
{
    _window = win;
    _data = dataContext;

    FindControls(win);
    ResetRuntimeState();
    PopulateStartYears();
    _config = CreateDefaultConfig(GetInitialStartYear());
    InitializeTemplateEditor();
    SyncUiFromConfig();
    AttachEvents(win);

    _grid.ItemsSource = _rows;

    CollectInputPaths();
    if (_paths.Count == 0)
    {
        _statusText.Text = "没有可处理的文件" + DescribeSkipped();
        _applyButton.IsEnabled = false;
        return;
    }

    StartScan();
}

public static bool OnButtonClicked(string controlName, object controlTag, Window win,
    IDictionary<string, object> dataContext, ICustomWindowContext winContext)
{
    // 本窗口的按钮事件已在 OnWindowLoaded 中通过 Click 注册。
    return false;
}

private static void FindControls(Window win)
{
    _grid = (DataGrid)win.FindName("PreviewGrid");
    _editor = (RichTextBox)win.FindName("TemplateEditor");
    _rootPathText = (TextBox)win.FindName("RootPathText");
    _digitsText = (TextBox)win.FindName("DigitsText");
    _prefixText = (TextBox)win.FindName("PrefixText");
    _suffixText = (TextBox)win.FindName("SuffixText");
    _rootModeCombo = (ComboBox)win.FindName("RootModeCombo");
    _startYearCombo = (ComboBox)win.FindName("StartYearCombo");
    _dateSourceCombo = (ComboBox)win.FindName("DateSourceCombo");
    _algorithmCombo = (ComboBox)win.FindName("AlgorithmCombo");
    _checksumBadge = (TextBlock)win.FindName("ChecksumBadge");
    _statusText = (TextBlock)win.FindName("StatusText");
    _progress = (ProgressBar)win.FindName("Progress");
    _browseRootButton = (Button)win.FindName("BrowseRootButton");
    _applyButton = (Button)win.FindName("BtnApply");
}

private static void ResetRuntimeState()
{
    _rows = new ObservableCollection<Dictionary<string, object>>();
    _files = new List<Dictionary<string, object>>();
    _paths = new List<string>();
    _pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    _hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    _scanning = false;
    _hashStop = false;
    _hashRunning = false;
    _hashAlgorithm = "";
    _scanGeneration++;
    _hashGeneration++;
    _hashTotal = 0;
    _hashDone = 0;
    _skippedFolders = 0;
    _skippedInvalid = 0;
    _lastCaret = null;
    _suppressEvents = false;
    _suppressEditor = false;
    if (_debounceTimer != null)
    {
        _debounceTimer.Stop();
        _debounceTimer = null;
    }
}

private static void PopulateStartYears()
{
    _startYearCombo.Items.Clear();
    int currentYear = DateTime.Now.Year;
    for (int year = currentYear; year >= 1900; year--)
        _startYearCombo.Items.Add(year);
}

private static int GetInitialStartYear()
{
    int year = 2000;
    if (_data != null)
    {
        object value;
        if (_data.TryGetValue("startYear", out value) && value != null)
        {
            if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out year))
                year = 2000;
        }
    }
    return NormalizeStartYear(year);
}

private static void AttachEvents(Window win)
{
    _rootModeCombo.SelectionChanged += RootModeCombo_SelectionChanged;
    _startYearCombo.SelectionChanged += StartYearCombo_SelectionChanged;
    _dateSourceCombo.SelectionChanged += DateSourceCombo_SelectionChanged;
    _algorithmCombo.SelectionChanged += AlgorithmCombo_SelectionChanged;
    _digitsText.TextChanged += DigitsText_TextChanged;
    _digitsText.LostFocus += DigitsText_LostFocus;
    _prefixText.TextChanged += PrefixText_TextChanged;
    _suffixText.TextChanged += SuffixText_TextChanged;
    _editor.TextChanged += TemplateEditor_TextChanged;
    _editor.SelectionChanged += TemplateEditor_SelectionChanged;
    _browseRootButton.Click += BrowseRootButton_Click;

    AttachClick("BtnTokenYyyy", TokenButton_Click);
    AttachClick("BtnTokenMm", TokenButton_Click);
    AttachClick("BtnTokenDd", TokenButton_Click);
    AttachClick("BtnTokenRoot", TokenButton_Click);
    AttachClick("BtnTokenName", TokenButton_Click);
    AttachClick("BtnTokenExt", TokenButton_Click);
    AttachClick("BtnTokenHash", TokenButton_Click);
    AttachClick("BtnResetTemplate", ResetTemplate_Click);
    AttachClick("BtnLoadPreset", LoadPreset_Click);
    AttachClick("BtnSavePreset", SavePreset_Click);
    AttachClick("BtnCancel", Cancel_Click);
    AttachClick("BtnApply", Apply_Click);

    win.Closed += Window_Closed;
}

private static void AttachClick(string name, RoutedEventHandler handler)
{
    Button button = _window.FindName(name) as Button;
    if (button != null)
        button.Click += handler;
}

// ---------------------------------------------------------------------------
// 控件初始化与配置同步
// ---------------------------------------------------------------------------

private static void InitializeTemplateEditor()
{
    _editor.Document = new FlowDocument();
    _editor.Document.PagePadding = new Thickness(0);
    _editor.Document.FontFamily = SystemFonts.MessageFontFamily;
    _editor.Document.FontSize = 12;
    _editor.AcceptsReturn = false;
    SetTemplateText(_config["template"]);
}

private static void SyncUiFromConfig()
{
    _suppressEvents = true;
    SelectComboByTag(_rootModeCombo, _config["rootMode"]);
    SelectStartYear(GetConfigInt(_config, "startYear", 2000));
    SelectComboByTag(_dateSourceCombo, _config["dateSource"]);
    SelectComboByTag(_algorithmCombo, _config["checksumAlgorithm"]);
    _digitsText.Text = _config["checksumDigits"];
    _prefixText.Text = _config["checksumPrefix"];
    _suffixText.Text = _config["checksumSuffix"];
    SetTemplateText(_config["template"]);
    _suppressEvents = false;

    UpdateRootPathText();
    UpdateChecksumBadge();
}

private static void SelectStartYear(int year)
{
    _startYearCombo.SelectedItem = year;
    if (_startYearCombo.SelectedItem == null)
    {
        _startYearCombo.Items.Insert(0, year);
        _startYearCombo.SelectedIndex = 0;
    }
}

private static string GetComboTag(ComboBox combo)
{
    if (combo == null)
        return null;
    ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
    if (item == null || item.Tag == null)
        return null;
    return Convert.ToString(item.Tag, CultureInfo.InvariantCulture);
}

private static void SelectComboByTag(ComboBox combo, string tag)
{
    foreach (object item in combo.Items)
    {
        ComboBoxItem comboItem = item as ComboBoxItem;
        if (comboItem != null && Convert.ToString(comboItem.Tag, CultureInfo.InvariantCulture) == tag)
        {
            combo.SelectedItem = comboItem;
            return;
        }
    }
    if (combo.Items.Count > 0)
        combo.SelectedIndex = 0;
}

private static void ReadConfigFromUi()
{
    _config["rootMode"] = GetComboTag(_rootModeCombo) ?? "current";
    // 根路径以 _config 为准，由文件夹选择器或预设更新；文本框仅用于显示，
    // 避免在“当前文件夹”模式下把占位提示误读为路径。

    int year = 2000;
    object selectedYear = _startYearCombo.SelectedItem;
    if (selectedYear != null)
        int.TryParse(Convert.ToString(selectedYear, CultureInfo.InvariantCulture),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out year);
    _config["startYear"] = year.ToString(CultureInfo.InvariantCulture);

    _config["dateSource"] = GetComboTag(_dateSourceCombo) ?? "filename";
    _config["checksumAlgorithm"] = GetComboTag(_algorithmCombo) ?? "CRC32";
    _config["checksumDigits"] = _digitsText.Text == null ? "" : _digitsText.Text.Trim();
    _config["checksumPrefix"] = _prefixText.Text == null ? "" : _prefixText.Text;
    _config["checksumSuffix"] = _suffixText.Text == null ? "" : _suffixText.Text;
    _config["template"] = GetTemplateText();
}

private static void UpdateRootPathText()
{
    if (_rootPathText == null)
        return;
    if (_config["rootMode"] == "custom")
    {
        _rootPathText.Text = _config["rootPath"] == null ? "" : _config["rootPath"];
        _rootPathText.Foreground = new SolidColorBrush(Color.FromRgb(52, 64, 84));
        _browseRootButton.IsEnabled = true;
    }
    else
    {
        _rootPathText.Text = "（每个源文件所在文件夹）";
        _rootPathText.Foreground = new SolidColorBrush(Color.FromRgb(102, 112, 133));
        _browseRootButton.IsEnabled = false;
    }
}

private static void UpdateChecksumBadge()
{
    string sample = MakeSampleHex(GetAlgorithmMaxDigits(_config["checksumAlgorithm"]));
    string badge = BuildChecksumBadge(_config["checksumAlgorithm"],
        GetConfigInt(_config, "checksumDigits", 8), _config["checksumPrefix"],
        _config["checksumSuffix"], sample);
    _checksumBadge.Text = badge;
    _checksumBadge.ToolTip = badge;
}

private static string MakeSampleHex(int length)
{
    StringBuilder builder = new StringBuilder();
    for (int i = 0; i < length / 2; i++)
        builder.Append("7f");
    if (length % 2 != 0)
        builder.Append('7');
    return builder.ToString();
}

// ---------------------------------------------------------------------------
// 配置变更
// ---------------------------------------------------------------------------

private static void RootModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents)
        return;

    string mode = GetComboTag(_rootModeCombo);
    if (mode == "custom")
    {
        if (!BrowseForRoot() && (_config["rootPath"] == null || _config["rootPath"].Length == 0))
        {
            _suppressEvents = true;
            SelectComboByTag(_rootModeCombo, "current");
            _suppressEvents = false;
            return;
        }
    }
    ApplyUiConfig();
}

private static void StartYearCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void DateSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void AlgorithmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void DigitsText_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEvents) return;
    ScheduleApply();
}

private static void DigitsText_LostFocus(object sender, RoutedEventArgs e)
{
    if (_suppressEvents) return;
    int digits;
    if (!int.TryParse(_digitsText.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out digits))
        digits = GetConfigInt(_config, "checksumDigits", 8);
    int max = GetAlgorithmMaxDigits(_config["checksumAlgorithm"]);
    if (digits < 1) digits = 1;
    if (digits > max) digits = max;
    _digitsText.Text = digits.ToString(CultureInfo.InvariantCulture);
    ApplyUiConfig();
}

private static void PrefixText_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEvents) return;
    ScheduleApply();
}

private static void SuffixText_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEvents) return;
    ScheduleApply();
}

private static void ScheduleApply()
{
    if (_suppressEvents) return;
    if (_debounceTimer == null)
    {
        _debounceTimer = new DispatcherTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(250);
        _debounceTimer.Tick += DebounceTimer_Tick;
    }
    _debounceTimer.Stop();
    _debounceTimer.Start();
}

private static void DebounceTimer_Tick(object sender, EventArgs e)
{
    if (_debounceTimer != null)
        _debounceTimer.Stop();
    ApplyUiConfig();
}

private static void ApplyUiConfig()
{
    if (_suppressEvents || _config == null)
        return;

    string oldStartYear = GetConfigString(_config, "startYear", "2000");
    string oldDateSource = GetConfigString(_config, "dateSource", "filename");
    string oldAlgorithm = GetConfigString(_config, "checksumAlgorithm", "CRC32");
    int oldDigits = GetConfigInt(_config, "checksumDigits", 8);
    bool oldNeedsHash = TemplateContainsToken(GetConfigString(_config, "template", DefaultTemplate), "【校验码】");

    ReadConfigFromUi();

    string newAlgorithm = NormalizeAlgorithm(_config["checksumAlgorithm"]);
    if (newAlgorithm != NormalizeAlgorithm(oldAlgorithm))
    {
        int oldMax = GetAlgorithmMaxDigits(oldAlgorithm);
        int newMax = GetAlgorithmMaxDigits(newAlgorithm);
        if (oldDigits == oldMax)
            _config["checksumDigits"] = newMax.ToString(CultureInfo.InvariantCulture);
    }

    NormalizeConfig(_config);
    UpdateChecksumBadge();
    UpdateRootPathText();

    if (!_digitsText.IsKeyboardFocusWithin)
        _digitsText.Text = _config["checksumDigits"];

    if (_scanning)
        return;

    bool newNeedsHash = TemplateContainsToken(_config["template"], "【校验码】");
    if (oldNeedsHash && !newNeedsHash)
    {
        _hashGeneration++;
        _hashRunning = false;
        _hashAlgorithm = "";
        _progress.Visibility = Visibility.Collapsed;
    }

    bool startChanged = oldStartYear != _config["startYear"];
    bool dateChanged = oldDateSource != _config["dateSource"];

    if (startChanged)
        RefreshFilenameDates();
    else if (dateChanged)
        RefreshEffectiveDates();
    else
        RecomputeAll();
}

private static void RefreshFilenameDates()
{
    int startYear = GetConfigInt(_config, "startYear", 2000);
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        DateTime parsed;
        DateTime? fileDate = null;
        if (TryExtractFilenameDate(Convert.ToString(item["FileName"], CultureInfo.InvariantCulture),
            startYear, out parsed))
            fileDate = parsed;
        item["FileNameDate"] = fileDate;
        UpdateEffectiveDate(item);
    }
    RecomputeAll();
}

private static void RefreshEffectiveDates()
{
    for (int i = 0; i < _files.Count; i++)
        UpdateEffectiveDate(_files[i]);
    RecomputeAll();
}

private static void UpdateEffectiveDate(Dictionary<string, object> item)
{
    string source = _config["dateSource"];
    string used = source;
    DateTime? value = null;

    if (source == "filename")
    {
        value = GetDate(item, "FileNameDate");
        if (!value.HasValue) { value = GetDate(item, "ExifDate"); used = "exif"; }
        if (!value.HasValue) { value = GetDate(item, "Modified"); used = "modified"; }
        if (!value.HasValue) { value = GetDate(item, "Created"); used = "created"; }
    }
    else if (source == "exif")
    {
        value = GetDate(item, "ExifDate");
        if (!value.HasValue) { value = GetDate(item, "Modified"); used = "modified"; }
        if (!value.HasValue) { value = GetDate(item, "Created"); used = "created"; }
    }
    else if (source == "modified")
    {
        value = GetDate(item, "Modified");
        if (!value.HasValue) { value = GetDate(item, "Created"); used = "created"; }
    }
    else
    {
        value = GetDate(item, "Created");
        used = "created";
    }

    item["EffectiveDate"] = value;
    item["EffectiveSource"] = used;
}

private static DateTime? GetDate(Dictionary<string, object> item, string key)
{
    object value;
    if (item.TryGetValue(key, out value) && value is DateTime)
        return (DateTime)value;
    return null;
}

// ---------------------------------------------------------------------------
// 文件扫描
// ---------------------------------------------------------------------------

private static void CollectInputPaths()
{
    if (_data == null)
        return;

    object filesObject;
    if (!_data.TryGetValue("files", out filesObject) || filesObject == null)
        return;

    if (filesObject is string)
    {
        AddInputPath((string)filesObject);
        return;
    }

    IEnumerable enumerable = filesObject as IEnumerable;
    if (enumerable == null)
    {
        AddInputPath(Convert.ToString(filesObject, CultureInfo.InvariantCulture));
        return;
    }

    foreach (object item in enumerable)
    {
        if (item == null)
            continue;
        AddInputPath(Convert.ToString(item, CultureInfo.InvariantCulture));
    }
}

private static void AddInputPath(string rawPath)
{
    if (rawPath == null)
        return;
    string pathText = rawPath.Trim();
    if (pathText.Length >= 2 && pathText[0] == '"' && pathText[pathText.Length - 1] == '"')
        pathText = pathText.Substring(1, pathText.Length - 2).Trim();
    if (pathText.Length == 0)
        return;

    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(pathText);
    }
    catch
    {
        _skippedInvalid++;
        return;
    }

    if (Directory.Exists(fullPath))
    {
        _skippedFolders++;
        return;
    }
    if (!File.Exists(fullPath))
    {
        _skippedInvalid++;
        return;
    }
    if (_pathSet.Add(fullPath))
        _paths.Add(fullPath);
}

private static void StartScan()
{
    _scanning = true;
    _scanGeneration++;
    int generation = _scanGeneration;
    List<string> paths = new List<string>(_paths);
    Dispatcher dispatcher = _window.Dispatcher;

    _progress.Visibility = Visibility.Visible;
    _progress.IsIndeterminate = true;
    _statusText.Text = "正在读取文件属性…";

    Thread thread = new Thread(delegate()
    {
        ScanWorker(paths, generation, dispatcher);
    });
    thread.IsBackground = true;
    thread.Start();
}

private static void ScanWorker(List<string> paths, int generation, Dispatcher dispatcher)
{
    List<Dictionary<string, object>> batch = new List<Dictionary<string, object>>();
    for (int i = 0; i < paths.Count; i++)
    {
        if (generation != _scanGeneration)
            return;

        Dictionary<string, object> item = BuildFileItem(paths[i]);
        if (item == null)
            continue;
        batch.Add(item);

        if (batch.Count >= 50)
        {
            List<Dictionary<string, object>> send = batch;
            batch = new List<Dictionary<string, object>>();
            Post(dispatcher, delegate()
            {
                if (generation == _scanGeneration)
                    AddFileBatch(send);
            });
        }
    }

    List<Dictionary<string, object>> finalBatch = batch;
    Post(dispatcher, delegate()
    {
        if (generation == _scanGeneration)
        {
            AddFileBatch(finalBatch);
            FinishScan(generation);
        }
    });
}

private static void AddFileBatch(List<Dictionary<string, object>> batch)
{
    for (int i = 0; i < batch.Count; i++)
    {
        Dictionary<string, object> item = batch[i];
        UpdateRowDates(item);
        item["Details"] = BuildDetails(item);
        _files.Add(item);
        _rows.Add(item);
    }
    if (_scanning)
        _statusText.Text = "正在读取文件属性… 已加载 " + _files.Count + " 个文件";
}

private static void FinishScan(int generation)
{
    if (generation != _scanGeneration)
        return;

    _scanning = false;
    _progress.Visibility = Visibility.Collapsed;

    int expected = _paths.Count;
    if (_files.Count < expected)
        _skippedInvalid += (expected - _files.Count);

    for (int i = 0; i < _files.Count; i++)
        UpdateRowDates(_files[i]);

    RecomputeAll();
}

private static Dictionary<string, object> BuildFileItem(string path)
{
    try
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists)
            return null;

        Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        item["SourcePath"] = info.FullName;
        item["Dir"] = info.DirectoryName;
        item["FileName"] = info.Name;
        item["BaseName"] = Path.GetFileNameWithoutExtension(info.Name);
        item["Ext"] = info.Extension == null ? "" : info.Extension.TrimStart('.');
        item["Size"] = info.Length;
        item["SizeText"] = FormatSize(info.Length);
        item["Created"] = info.CreationTime;
        item["Modified"] = info.LastWriteTime;
        item["ExifDate"] = TryReadExifDate(info.FullName);
        item["FileNameDate"] = null;
        item["EffectiveDate"] = null;
        item["EffectiveSource"] = "";
        item["HashHex"] = null;
        item["HashPending"] = false;
        item["TargetPath"] = "";
        item["Status"] = "";
        item["Error"] = "";
        item["HasError"] = false;
        item["IsPending"] = false;
        item["Details"] = "";
        return item;
    }
    catch
    {
        return null;
    }
}

private static void UpdateRowDates(Dictionary<string, object> item)
{
    int startYear = GetConfigInt(_config, "startYear", 2000);
    DateTime parsed;
    DateTime? fileDate = null;
    if (TryExtractFilenameDate(Convert.ToString(item["FileName"], CultureInfo.InvariantCulture),
        startYear, out parsed))
        fileDate = parsed;
    item["FileNameDate"] = fileDate;
    UpdateEffectiveDate(item);
}

private static string FormatSize(long size)
{
    if (size < 1024)
        return size.ToString(CultureInfo.InvariantCulture) + " B";
    double value = size / 1024.0;
    if (value < 1024.0)
        return value.ToString("0.0", CultureInfo.CurrentCulture) + " KB";
    value = value / 1024.0;
    if (value < 1024.0)
        return value.ToString("0.0", CultureInfo.CurrentCulture) + " MB";
    value = value / 1024.0;
    return value.ToString("0.0", CultureInfo.CurrentCulture) + " GB";
}

private static DateTime? TryReadExifDate(string path)
{
    string extension = Path.GetExtension(path);
    if (extension == null)
        return null;
    extension = extension.ToLowerInvariant();
    bool supported = false;
    for (int i = 0; i < ExifExtensions.Length; i++)
    {
        if (ExifExtensions[i] == extension) { supported = true; break; }
    }
    if (!supported)
        return null;

    try
    {
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;
            BitmapMetadata metadata = decoder.Frames[0].Metadata as BitmapMetadata;
            if (metadata == null)
                return null;

            string[] queries = new string[] {
                "/app1/ifd/exif/{ushort=36867}",
                "/app1/ifd/exif/{ushort=36868}",
                "/app1/ifd/{ushort=306}"
            };
            for (int i = 0; i < queries.Length; i++)
            {
                object value = null;
                try
                {
                    value = metadata.GetQuery(queries[i]);
                }
                catch
                {
                    value = null;
                }
                if (value is DateTime)
                    return (DateTime)value;
                string text = value as string;
                if (text != null)
                {
                    DateTime parsed;
                    if (TryParseExifDateText(text, out parsed))
                        return parsed;
                }
            }
        }
    }
    catch
    {
        return null;
    }
    return null;
}

private static bool TryParseExifDateText(string text, out DateTime result)
{
    result = DateTime.MinValue;
    if (text == null)
        return false;
    string value = text.Trim().Trim('\0').Trim();
    if (value.Length == 0)
        return false;

    string[] formats = new string[] {
        "yyyy:MM:dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy:MM:dd",
        "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm:ss"
    };
    if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
        DateTimeStyles.None, out result))
        return true;
    return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
}

private static void Post(Dispatcher dispatcher, Action action)
{
    if (dispatcher == null || action == null)
        return;
    try
    {
        dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }
    catch
    {
        // 窗口已关闭时忽略。
    }
}

// ---------------------------------------------------------------------------
// 校验码后台计算
// ---------------------------------------------------------------------------

private static void EnsureHashWorker()
{
    if (_scanning || _files.Count == 0 || _window == null)
        return;

    if (!TemplateContainsToken(_config["template"], "【校验码】"))
    {
        _hashTotal = 0;
        _hashDone = 0;
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

    string algorithm = _config["checksumAlgorithm"];
    if (_hashRunning && _hashAlgorithm == algorithm)
        return;

    List<string> missing = new List<string>();
    for (int i = 0; i < _files.Count; i++)
    {
        string path = Convert.ToString(_files[i]["SourcePath"], CultureInfo.InvariantCulture);
        if (!IsHashCached(path, algorithm))
            missing.Add(path);
    }

    if (missing.Count == 0)
    {
        _hashTotal = 0;
        _hashDone = 0;
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

    _hashGeneration++;
    int generation = _hashGeneration;
    _hashStop = false;
    _hashRunning = true;
    _hashAlgorithm = algorithm;
    _hashTotal = missing.Count;
    _hashDone = 0;

    _progress.Visibility = Visibility.Visible;
    _progress.IsIndeterminate = false;
    _progress.Minimum = 0;
    _progress.Maximum = _hashTotal;
    _progress.Value = 0;

    Dispatcher dispatcher = _window.Dispatcher;
    Thread thread = new Thread(delegate()
    {
        HashWorker(generation, algorithm, missing, dispatcher);
    });
    thread.IsBackground = true;
    thread.Start();
}

private static string HashKey(string path, string algorithm)
{
    return path + "|" + NormalizeAlgorithm(algorithm);
}

private static bool IsHashCached(string path, string algorithm)
{
    lock (_hashLock)
    {
        return _hashCache.ContainsKey(HashKey(path, algorithm));
    }
}

private static string GetCachedHash(string path, string algorithm, out bool isError)
{
    isError = false;
    lock (_hashLock)
    {
        string value;
        if (!_hashCache.TryGetValue(HashKey(path, algorithm), out value))
            return null;
        if (value == "!ERR")
        {
            isError = true;
            return null;
        }
        return value;
    }
}

private static void HashWorker(int generation, string algorithm, List<string> paths, Dispatcher dispatcher)
{
    try
    {
        for (int i = 0; i < paths.Count; i++)
        {
            if (_hashStop || generation != _hashGeneration)
                return;

            string error;
            string hash = ComputeHashHex(paths[i], algorithm, out error);
            lock (_hashLock)
            {
                _hashCache[HashKey(paths[i], algorithm)] = hash == null ? "!ERR" : hash;
            }

            if (_hashStop || generation != _hashGeneration)
                return;

            _hashDone++;
            if (_hashDone % 5 == 0 || _hashDone >= _hashTotal)
            {
                Post(dispatcher, delegate()
                {
                    if (generation == _hashGeneration)
                        UpdateHashProgress();
                });
            }
        }
    }
    finally
    {
        if (generation == _hashGeneration)
            _hashRunning = false;
    }
}

private static void UpdateHashProgress()
{
    if (_hashTotal <= 0)
        return;

    _progress.Visibility = Visibility.Visible;
    _progress.IsIndeterminate = false;
    _progress.Minimum = 0;
    _progress.Maximum = _hashTotal;
    _progress.Value = Math.Min(_hashDone, _hashTotal);

    if (_hashDone < _hashTotal)
    {
        _statusText.Text = "正在计算校验码 " + _hashDone + "/" + _hashTotal;
        return;
    }

    _progress.Visibility = Visibility.Collapsed;
    RecomputeAll();
}

// ---------------------------------------------------------------------------
// 目标路径实时计算
// ---------------------------------------------------------------------------

private static void RecomputeAll()
{
    if (_grid == null || _scanning)
        return;

    string template = _config["template"];
    string badToken;
    bool templateValid = ValidateTemplateTokens(template, out badToken);
    bool needsHash = TemplateContainsToken(template, "【校验码】");

    string algorithm = _config["checksumAlgorithm"];
    int digits = GetConfigInt(_config, "checksumDigits", 8);
    string prefix = _config["checksumPrefix"];
    string suffix = _config["checksumSuffix"];
    string rootMode = _config["rootMode"];
    string customRoot = _config["rootPath"];
    bool customRootMissing = (rootMode == "custom" &&
        (customRoot == null || customRoot.Trim().Length == 0));

    // 第一遍：计算每个文件的目标路径。
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        item["HasError"] = false;
        item["IsPending"] = false;
        item["Error"] = "";
        item["Status"] = "就绪";
        item["TargetPath"] = "";
        item["HashHex"] = null;
        item["HashPending"] = false;

        if (!templateValid)
        {
            SetRowError(item, badToken == null || badToken.Length == 0
                ? "目标路径模板为空" : "模板包含未知胶囊：" + badToken, "模板错误");
            continue;
        }

        string rootDirectory = rootMode == "custom"
            ? customRoot : Convert.ToString(item["Dir"], CultureInfo.InvariantCulture);
        if (rootMode == "custom" && customRootMissing)
        {
            SetRowError(item, "根路径为其他文件夹，但尚未选择文件夹", "根路径错误");
            continue;
        }
        if (rootDirectory == null || rootDirectory.Trim().Length == 0)
        {
            SetRowError(item, "无法确定根路径", "根路径错误");
            continue;
        }

        DateTime? effectiveDate = GetDate(item, "EffectiveDate");
        if (!effectiveDate.HasValue)
        {
            SetRowError(item, "没有可用日期", "日期错误");
            continue;
        }

        bool hashPending = false;
        string checksum = "";
        if (needsHash)
        {
            string path = Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture);
            bool hashError;
            string hash = GetCachedHash(path, algorithm, out hashError);
            if (hashError)
            {
                SetRowError(item, "校验码计算失败", "校验码失败");
                continue;
            }
            if (hash == null)
            {
                hashPending = true;
                checksum = BuildPendingChecksumBadge(algorithm, digits, prefix, suffix);
            }
            else
            {
                item["HashHex"] = hash;
                checksum = BuildChecksumBadge(algorithm, digits, prefix, suffix, hash);
            }
        }

        string baseName = Convert.ToString(item["BaseName"], CultureInfo.InvariantCulture);
        string extension = Convert.ToString(item["Ext"], CultureInfo.InvariantCulture);
        string target;
        string error;
        bool ok = TryBuildTargetPath(template, rootDirectory, effectiveDate.Value,
            baseName, extension, checksum, out target, out error);

        if (!ok)
        {
            SetRowError(item, error == null ? "无法生成目标路径" : error, "路径错误");
            continue;
        }

        item["TargetPath"] = target;
        if (hashPending)
        {
            item["IsPending"] = true;
            item["HashPending"] = true;
            item["Status"] = "校验码计算中";
        }
        else
        {
            item["Status"] = "就绪";
        }
    }

    // 第二遍：检测批内重复目标。
    Dictionary<string, List<Dictionary<string, object>>> targetMap =
        new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError") || GetBool(item, "IsPending"))
            continue;
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (target == null || target.Length == 0)
            continue;
        List<Dictionary<string, object>> list;
        if (!targetMap.TryGetValue(target, out list))
        {
            list = new List<Dictionary<string, object>>();
            targetMap[target] = list;
        }
        list.Add(item);
    }
    foreach (KeyValuePair<string, List<Dictionary<string, object>>> pair in targetMap)
    {
        if (pair.Value.Count <= 1)
            continue;
        for (int i = 0; i < pair.Value.Count; i++)
            SetRowError(pair.Value[i], "目标路径重复：" + pair.Key, "目标重复");
    }

    // 第三遍：检测目标已存在。
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError") || GetBool(item, "IsPending"))
            continue;

        string source = Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture);
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (target == null || target.Length == 0)
            continue;

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            item["Status"] = "无变化";
            continue;
        }

        try
        {
            if (File.Exists(target) || Directory.Exists(target))
                SetRowError(item, "目标文件或目录已存在：" + target, "目标已存在");
        }
        catch
        {
            SetRowError(item, "无法检查目标路径是否存在", "路径错误");
        }
    }

    for (int i = 0; i < _files.Count; i++)
        RefreshRowDetails(_files[i]);

    _grid.Items.Refresh();
    UpdateStatus();

    int statusReady;
    int statusErrors;
    int statusPending;
    CountRows(out statusReady, out statusErrors, out statusPending);
    if (statusPending == 0)
        _progress.Visibility = Visibility.Collapsed;

    if (needsHash && templateValid)
        EnsureHashWorker();
    else
    {
        _hashTotal = 0;
        _hashDone = 0;
        _progress.Visibility = Visibility.Collapsed;
    }
}

private static void RefreshRowDetails(Dictionary<string, object> item)
{
    item["Details"] = BuildDetails(item);
}

private static void SetRowError(Dictionary<string, object> item, string message, string status)
{
    item["HasError"] = true;
    item["IsPending"] = false;
    item["Error"] = message;
    item["Status"] = status;
}

private static bool GetBool(Dictionary<string, object> item, string key)
{
    object value;
    if (item.TryGetValue(key, out value) && value is bool)
        return (bool)value;
    return false;
}

private static string BuildDetails(Dictionary<string, object> item)
{
    StringBuilder builder = new StringBuilder();
    builder.AppendLine("源文件：" + Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture));
    builder.AppendLine("所在目录：" + Convert.ToString(item["Dir"], CultureInfo.InvariantCulture));
    builder.AppendLine("大小：" + Convert.ToString(item["SizeText"], CultureInfo.InvariantCulture) +
        "（" + Convert.ToString(item["Size"], CultureInfo.InvariantCulture) + " 字节）");
    builder.AppendLine("创建日期：" + FormatDate(item["Created"]));
    builder.AppendLine("修改日期：" + FormatDate(item["Modified"]));
    builder.AppendLine("EXIF拍摄日期：" + FormatDate(item["ExifDate"]));
    builder.AppendLine("文件名中的日期：" + FormatDate(item["FileNameDate"]));
    builder.AppendLine("生效日期：" + FormatDate(item["EffectiveDate"]) +
        "（来源：" + GetDateSourceName(Convert.ToString(item["EffectiveSource"], CultureInfo.InvariantCulture)) + "）");
    builder.AppendLine("扩展名：" + Convert.ToString(item["Ext"], CultureInfo.InvariantCulture));

    string hash = Convert.ToString(item["HashHex"], CultureInfo.InvariantCulture);
    if (!TemplateContainsToken(_config["template"], "【校验码】"))
        builder.AppendLine("校验码：未使用");
    else if (GetBool(item, "IsPending"))
        builder.AppendLine("校验码：" + _config["checksumAlgorithm"] + "（计算中）");
    else if (hash == null || hash.Length == 0)
        builder.AppendLine("校验码：" + _config["checksumAlgorithm"] + "（未计算）");
    else
        builder.AppendLine("校验码：" + _config["checksumAlgorithm"] + " = " + hash);

    builder.AppendLine("目标路径：" + Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture));
    string error = Convert.ToString(item["Error"], CultureInfo.InvariantCulture);
    builder.AppendLine("状态：" + Convert.ToString(item["Status"], CultureInfo.InvariantCulture) +
        (error == null || error.Length == 0 ? "" : "；" + error));
    return builder.ToString();
}

private static string FormatDate(object value)
{
    if (value is DateTime)
        return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    return "—";
}

private static string GetDateSourceName(string source)
{
    if (source == "filename") return "文件名中的日期";
    if (source == "exif") return "EXIF拍摄日期";
    if (source == "modified") return "修改日期";
    if (source == "created") return "创建日期";
    return "—";
}

private static void UpdateStatus()
{
    int ready;
    int errors;
    int pending;
    CountRows(out ready, out errors, out pending);

    if (_scanning)
    {
        _statusText.Text = "正在读取文件属性… 已加载 " + _files.Count + " 个文件";
        _applyButton.IsEnabled = false;
        return;
    }

    if (_files.Count == 0)
    {
        _statusText.Text = "没有可处理的文件" + DescribeSkipped();
        _applyButton.IsEnabled = false;
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

    string text = "共 " + _files.Count + " 个文件" + DescribeSkipped();
    text += "；就绪 " + ready + "；异常 " + errors;
    if (pending > 0)
        text += "；校验码计算中 " + pending;
    if (TemplateContainsToken(_config["template"], "【校验码】") && _hashTotal > 0 &&
        _hashDone < _hashTotal)
        text += "（" + _hashDone + "/" + _hashTotal + "）";
    _statusText.Text = text;

    _applyButton.IsEnabled = (!_scanning && pending == 0 && ready > 0);
    if (!(pending > 0 && TemplateContainsToken(_config["template"], "【校验码】")))
        _progress.Visibility = Visibility.Collapsed;
}

private static void CountRows(out int ready, out int errors, out int pending)
{
    ready = 0;
    errors = 0;
    pending = 0;
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError"))
            errors++;
        else if (GetBool(item, "IsPending"))
            pending++;
        else
        {
            string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
            if (target != null && target.Length > 0)
                ready++;
        }
    }
}

private static string DescribeSkipped()
{
    StringBuilder builder = new StringBuilder();
    if (_skippedFolders > 0)
        builder.Append("；跳过文件夹 " + _skippedFolders + " 个");
    if (_skippedInvalid > 0)
        builder.Append("；跳过无效路径 " + _skippedInvalid + " 个");
    return builder.ToString();
}

// ---------------------------------------------------------------------------
// 预设、文件选择与输出
// ---------------------------------------------------------------------------

private static bool BrowseForRoot()
{
    FolderBrowserDialog dialog = new FolderBrowserDialog();
    dialog.Description = "选择目标根路径";
    dialog.ShowNewFolderButton = true;
    if (_config["rootPath"] != null && _config["rootPath"].Length > 0)
    {
        try
        {
            dialog.SelectedPath = _config["rootPath"];
        }
        catch
        {
        }
    }
    if (dialog.ShowDialog() == DialogResult.OK)
    {
        _config["rootPath"] = dialog.SelectedPath;
        _rootPathText.Text = dialog.SelectedPath;
        return true;
    }
    return false;
}

private static void BrowseRootButton_Click(object sender, RoutedEventArgs e)
{
    if (BrowseForRoot())
        ApplyUiConfig();
}

private static void LoadPreset_Click(object sender, RoutedEventArgs e)
{
    if (_data == null)
        return;

    object presetObject;
    if (!_data.TryGetValue("preset", out presetObject) || presetObject == null)
    {
        MessageBox.Show("【批量重命名器预设】为空，无法载入。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    IDictionary<string, object> preset = presetObject as IDictionary<string, object>;
    if (preset == null)
    {
        IDictionary dictionary = presetObject as IDictionary;
        if (dictionary != null)
        {
            Dictionary<string, object> converted =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in dictionary)
                converted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
            preset = converted;
        }
    }

    if (preset == null || preset.Count == 0)
    {
        MessageBox.Show("【批量重命名器预设】格式无效或为空。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (!ApplyPresetDictionary(preset, _config))
    {
        MessageBox.Show("预设中没有可识别的配置项。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (_debounceTimer != null)
        _debounceTimer.Stop();
    SyncUiFromConfig();
    if (_scanning)
    {
        _statusText.Text = "已载入预设，文件属性读取完成后刷新预览";
        return;
    }
    RefreshFilenameDates();
    _statusText.Text = "已载入预设";
}

private static void SavePreset_Click(object sender, RoutedEventArgs e)
{
    if (_debounceTimer != null)
        _debounceTimer.Stop();
    ReadConfigFromUi();
    NormalizeConfig(_config);
    if (_data != null)
        _data["preset"] = BuildPresetDictionary(_config);
    _statusText.Text = "预设已保存，将在窗口关闭后输出";
}

private static void Apply_Click(object sender, RoutedEventArgs e)
{
    if (_scanning)
    {
        MessageBox.Show("正在读取文件属性，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    // 立即吸收尚未到防抖时间的文本修改，避免输出旧配置。
    if (_debounceTimer != null)
        _debounceTimer.Stop();
    ApplyUiConfig();

    RecomputeAll();

    int ready;
    int errors;
    int pending;
    CountRows(out ready, out errors, out pending);

    if (pending > 0)
    {
        MessageBox.Show("校验码仍在计算中，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }
    if (ready == 0)
    {
        MessageBox.Show("没有可输出的文件，请检查模板与文件状态。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }
    if (errors > 0)
    {
        DialogResult answer = MessageBox.Show(
            "有 " + errors + " 个文件存在异常，将仅输出 " + ready + " 个就绪文件。是否继续？",
            "批量重命名器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;
    }

    List<object> originalPaths = new List<object>();
    List<object> targetPaths = new List<object>();
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError") || GetBool(item, "IsPending"))
            continue;
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (target == null || target.Length == 0)
            continue;
        originalPaths.Add(Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture));
        targetPaths.Add(target);
    }

    if (_data != null)
    {
        _data["origPaths"] = originalPaths;
        _data["targetPaths"] = targetPaths;
        _data["preset"] = BuildPresetDictionary(_config);
    }

    if (_window != null)
        _window.Close();
}

private static void Cancel_Click(object sender, RoutedEventArgs e)
{
    if (_window != null)
        _window.Close();
}

// ---------------------------------------------------------------------------
// 模板编辑器（胶囊）
// ---------------------------------------------------------------------------

private static void TemplateEditor_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEditor || _suppressEvents)
        return;
    ScheduleApply();
}

private static void TemplateEditor_SelectionChanged(object sender, RoutedEventArgs e)
{
    if (!_suppressEditor && _editor != null)
        _lastCaret = _editor.CaretPosition;
}

private static void TokenButton_Click(object sender, RoutedEventArgs e)
{
    Button button = sender as Button;
    if (button == null || button.Tag == null)
        return;
    InsertTemplateToken(Convert.ToString(button.Tag, CultureInfo.InvariantCulture));
}

private static void ResetTemplate_Click(object sender, RoutedEventArgs e)
{
    SetTemplateText(DefaultTemplate);
    ScheduleApply();
}

private static void InsertTemplateToken(string token)
{
    if (_editor == null || token == null || !IsKnownToken(token))
        return;

    try
    {
        _editor.Focus();
        TextPointer caret = _lastCaret;
        if (caret == null)
            caret = _editor.CaretPosition;
        if (caret.Paragraph == null)
            caret = _editor.Document.ContentEnd;

        InlineUIContainer owner = caret.Parent as InlineUIContainer;
        if (owner != null)
            caret = owner.ElementEnd;

        Border chip = CreateChip(token);
        InlineUIContainer inserted = new InlineUIContainer(chip, caret);
        _editor.CaretPosition = inserted.ElementEnd;
        _lastCaret = inserted.ElementEnd;
        ScheduleApply();
    }
    catch
    {
        // 极端情况下退化为追加文本，避免影响其他操作。
        Paragraph paragraph = new Paragraph(new Run(token));
        _editor.Document.Blocks.Add(paragraph);
        ScheduleApply();
    }
}

private static Border CreateChip(string token)
{
    Border border = new Border();
    border.Background = new SolidColorBrush(Color.FromRgb(234, 242, 255));
    border.BorderBrush = new SolidColorBrush(Color.FromRgb(187, 212, 248));
    border.BorderThickness = new Thickness(1);
    border.CornerRadius = new CornerRadius(9);
    border.Padding = new Thickness(6, 1, 6, 1);
    border.Margin = new Thickness(1, 0, 1, 0);
    border.Tag = token;

    TextBlock text = new TextBlock();
    text.Text = token;
    text.FontSize = 11;
    text.Foreground = new SolidColorBrush(Color.FromRgb(23, 92, 211));
    text.VerticalAlignment = VerticalAlignment.Center;
    border.Child = text;
    return border;
}

private static void SetTemplateText(string template)
{
    if (_editor == null)
        return;

    _suppressEditor = true;
    try
    {
        string text = template == null ? "" : template;
        Paragraph paragraph = new Paragraph();
        paragraph.Margin = new Thickness(0);

        int position = 0;
        MatchCollection matches = EditorTokenRegex.Matches(text);
        for (int i = 0; i < matches.Count; i++)
        {
            Match match = matches[i];
            if (match.Index > position)
                paragraph.Inlines.Add(new Run(text.Substring(position, match.Index - position)));

            if (IsKnownToken(match.Value))
                paragraph.Inlines.Add(new InlineUIContainer(CreateChip(match.Value)));
            else
                paragraph.Inlines.Add(new Run(match.Value));

            position = match.Index + match.Length;
        }
        if (position < text.Length)
            paragraph.Inlines.Add(new Run(text.Substring(position)));
        if (paragraph.Inlines.Count == 0)
            paragraph.Inlines.Add(new Run(""));

        _editor.Document = new FlowDocument(paragraph);
        _editor.Document.PagePadding = new Thickness(0);
        _editor.Document.FontFamily = SystemFonts.MessageFontFamily;
        _editor.Document.FontSize = 12;
        _lastCaret = paragraph.ContentEnd;
        _editor.CaretPosition = paragraph.ContentEnd;
    }
    catch
    {
        // 忽略模板渲染失败，保留原配置值。
    }
    finally
    {
        _suppressEditor = false;
    }
}

private static string GetTemplateText()
{
    if (_editor == null)
        return GetConfigString(_config, "template", DefaultTemplate);

    StringBuilder builder = new StringBuilder();
    bool firstBlock = true;
    foreach (Block block in _editor.Document.Blocks)
    {
        Paragraph paragraph = block as Paragraph;
        if (paragraph == null)
            continue;
        if (!firstBlock)
            builder.Append('\n');
        firstBlock = false;
        foreach (Inline inline in paragraph.Inlines)
            AppendInlineText(inline, builder);
    }
    return builder.ToString();
}

private static void AppendInlineText(Inline inline, StringBuilder builder)
{
    if (inline == null)
        return;

    Run run = inline as Run;
    if (run != null)
    {
        builder.Append(run.Text);
        return;
    }

    InlineUIContainer container = inline as InlineUIContainer;
    if (container != null)
    {
        Border border = container.Child as Border;
        if (border != null && border.Tag != null)
            builder.Append(Convert.ToString(border.Tag, CultureInfo.InvariantCulture));
        return;
    }

    Span span = inline as Span;
    if (span != null)
    {
        foreach (Inline child in span.Inlines)
            AppendInlineText(child, builder);
    }
}

// ---------------------------------------------------------------------------
// 关闭
// ---------------------------------------------------------------------------

private static void Window_Closed(object sender, EventArgs e)
{
    Window closedWindow = sender as Window;
    if (closedWindow != null && _window != null && !object.ReferenceEquals(closedWindow, _window))
        return;

    _scanGeneration++;
    _hashGeneration++;
    _hashStop = true;
    _hashRunning = false;
    _hashAlgorithm = "";
    if (_debounceTimer != null)
    {
        _debounceTimer.Stop();
        _debounceTimer = null;
    }

    _window = null;
    _grid = null;
    _editor = null;
    _rootPathText = null;
    _digitsText = null;
    _prefixText = null;
    _suffixText = null;
    _rootModeCombo = null;
    _startYearCombo = null;
    _dateSourceCombo = null;
    _algorithmCombo = null;
    _checksumBadge = null;
    _statusText = null;
    _progress = null;
    _browseRootButton = null;
    _applyButton = null;
    _rows = new ObservableCollection<Dictionary<string, object>>();
    _files = new List<Dictionary<string, object>>();
    _paths = new List<string>();
    _pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    _hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    _config = null;
}
