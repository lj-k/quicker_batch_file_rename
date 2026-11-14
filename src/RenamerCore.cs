using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// ============================================================================
// 批量重命名器核心逻辑（Quicker 辅助 C# 代码片段）
// 版本：0.02
// 说明：本文件不包含 namespace / class 声明，部署时会与 CustomWindow.Aux.cs
//       合并后放入 Quicker【自定义窗口】的【辅助C#代码】中。
// 兼容性：按 C# 5.0 / .NET Framework 4.x 语法编写。
// ============================================================================

public const string DefaultTemplate = "【根路径】/【日期yyyy】.【日期mm】/aaa/bbb/【源文件名】-【校验码】.【扩展名】";

public static readonly string[] SupportedAlgorithms = new string[] { "CRC32", "BLAKE3", "MD5", "SHA-256" };
public static readonly string[] SupportedDateSources = new string[] { "filename", "exif", "modified", "created" };
public static readonly string[] TemplateTokens = new string[] {
    "【日期yyyy】", "【日期mm】", "【日期dd】", "【根路径】",
    "【源文件名】", "【扩展名】", "【校验码】"
};

private static readonly Regex FilenameDate1Regex =
    new Regex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
private static readonly Regex FilenameDate2Regex =
    new Regex(@"(?<!\d)(\d{8})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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

public static string NormalizeFallbackOrder(string order)
{
    List<string> result = new List<string>();
    if (order != null)
    {
        string[] parts = order.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i] == null ? "" : parts[i].Trim();
            if (part.Length == 0)
                continue;
            string normalized = NormalizeDateSource(part);
            if (!result.Contains(normalized))
                result.Add(normalized);
        }
    }
    for (int i = 0; i < SupportedDateSources.Length; i++)
    {
        if (!result.Contains(SupportedDateSources[i]))
            result.Add(SupportedDateSources[i]);
    }
    return string.Join(",", result.ToArray());
}

public static bool TryResolveEffectiveDate(IDictionary<string, DateTime?> dates,
    string primarySource, bool fallbackEnabled, string fallbackOrder,
    out DateTime? effectiveDate, out string effectiveSource)
{
    effectiveDate = null;
    effectiveSource = "";

    if (dates == null)
        return false;

    string primary = NormalizeDateSource(primarySource);
    DateTime? value;
    if (dates.TryGetValue(primary, out value) && value.HasValue)
    {
        effectiveDate = value;
        effectiveSource = primary;
        return true;
    }

    if (!fallbackEnabled)
        return false;

    string[] order = NormalizeFallbackOrder(fallbackOrder).Split(',');
    for (int i = 0; i < order.Length; i++)
    {
        string source = order[i];
        if (source == primary)
            continue;
        if (dates.TryGetValue(source, out value) && value.HasValue)
        {
            effectiveDate = value;
            effectiveSource = source;
            return true;
        }
    }
    return false;
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
    return left + label + "-" + code + right;
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
    return left + label + "-…" + right;
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

public static bool TryBuildTargetPath(string template, string rootDirectory, DateTime? effectiveDate,
    string missingDateText, string baseName, string extension, string checksumBadge,
    out string targetPath, out string error)
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

    string dateYear;
    string dateMonth;
    string dateDay;
    if (effectiveDate.HasValue)
    {
        dateYear = effectiveDate.Value.ToString("yyyy", CultureInfo.InvariantCulture);
        dateMonth = effectiveDate.Value.ToString("MM", CultureInfo.InvariantCulture);
        dateDay = effectiveDate.Value.ToString("dd", CultureInfo.InvariantCulture);
    }
    else
    {
        dateYear = missingDateText == null ? "" : missingDateText;
        dateMonth = dateYear;
        dateDay = dateYear;
    }

    Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
    values["【日期yyyy】"] = dateYear;
    values["【日期mm】"] = dateMonth;
    values["【日期dd】"] = dateDay;
    values["【根路径】"] = rootDirectory == null ? "" : rootDirectory;
    values["【源文件名】"] = baseName == null ? "" : baseName;
    values["【扩展名】"] = extension == null ? "" : extension;
    values["【校验码】"] = checksumBadge == null ? "" : checksumBadge;

    string rendered = RenderTemplate(template, values);
    string normalized = NormalizeTargetPath(rendered, rootDirectory, out error);
    if (normalized == null)
        return false;

    if (!ValidateCrossPlatformPath(normalized, out error))
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
    if (IsAsciiDriveLetter(path[0]) && path[1] == ':')
        return true;
    if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
        return true;
    return false;
}

private static bool IsAsciiDriveLetter(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

public static bool ValidateCrossPlatformPath(string path, out string error)
{
    error = null;
    if (path == null || path.Trim().Length == 0)
    {
        error = "目标路径为空";
        return false;
    }

    List<string> problems = new List<string>();
    if (path.IndexOf('/') >= 0)
        problems.Add("目标路径包含未规范化的斜杠");
    if (path[path.Length - 1] == '\\')
        problems.Add("目标路径以目录分隔符结尾，缺少文件名");

    bool isUnc = path.StartsWith("\\\\", StringComparison.Ordinal);
    bool isDrive = path.Length >= 3 && IsAsciiDriveLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    if (!isUnc && !isDrive)
        problems.Add("目标路径不是绝对路径（应为 X:\\... 或 \\\\服务器\\共享\\...）");

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
            problems.Add("UNC 目标路径至少需要服务器、共享和文件名");
    }

    StringBuilder invalidChars = new StringBuilder();
    HashSet<string> reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    bool hasDotSegment = false;
    bool hasTrailingIssue = false;
    bool hasSegmentLengthIssue = false;

    for (int i = 0; i < segments.Length; i++)
    {
        string segment = segments[i];
        if (segment.Length == 0)
            continue;
        if (i == 0 && isDrive && segment.Length == 2 && segment[1] == ':')
            continue;

        if (segment == "." || segment == "..")
            hasDotSegment = true;

        if (segment[segment.Length - 1] == ' ' || segment[segment.Length - 1] == '.')
            hasTrailingIssue = true;

        if (segment.Length > 255 || Encoding.UTF8.GetByteCount(segment) > 255)
            hasSegmentLengthIssue = true;

        for (int j = 0; j < segment.Length; j++)
        {
            char c = segment[j];
            if (IsCrossPlatformInvalidChar(c))
            {
                if (invalidChars.ToString().IndexOf(c) < 0)
                    invalidChars.Append(c);
            }
        }

        string reserved = GetReservedDeviceName(segment);
        if (reserved != null)
            reservedNames.Add(reserved);
    }

    if (hasDotSegment)
        problems.Add("目标路径不能包含 . 或 .. 目录段");
    if (hasTrailingIssue)
        problems.Add("路径段不能以空格或点结尾");
    if (hasSegmentLengthIssue)
        problems.Add("存在路径段超过255字符或255字节");
    if (invalidChars.Length > 0)
        problems.Add("路径段包含跨平台非法字符：" + invalidChars.ToString());
    if (reservedNames.Count > 0)
    {
        string[] names = new string[reservedNames.Count];
        reservedNames.CopyTo(names);
        problems.Add("路径段包含Windows保留设备名：" + string.Join("、", names));
    }
    if (path.Length > 259)
        problems.Add("总路径超过Windows 259字符限制");
    if (Encoding.UTF8.GetByteCount(path) > 1024)
        problems.Add("总路径超过macOS 1024字节限制");

    string fileName = GetLastPathSegment(path);
    if (fileName == null || fileName.Length == 0)
        problems.Add("目标路径缺少文件名");

    if (problems.Count > 0)
    {
        error = string.Join("；", problems.ToArray());
        return false;
    }
    return true;
}

private static bool IsCrossPlatformInvalidChar(char c)
{
    if (c < 32)
        return true;
    return c == '<' || c == '>' || c == ':' || c == '"' ||
           c == '|' || c == '?' || c == '*';
}

private static string GetReservedDeviceName(string segment)
{
    if (segment == null || segment.Length == 0)
        return null;

    int dot = segment.IndexOf('.');
    string baseName = (dot >= 0 ? segment.Substring(0, dot) : segment).Trim();
    if (baseName.Length == 0)
        return null;
    string upper = baseName.ToUpperInvariant();

    if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" ||
        upper == "CLOCK$" || upper == "CONIN$" || upper == "CONOUT$")
        return baseName;

    if (upper.Length == 4)
    {
        string prefix = upper.Substring(0, 3);
        char number = upper[3];
        if ((prefix == "COM" || prefix == "LPT") && number >= '0' && number <= '9')
            return baseName;
    }
    return null;
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
    config["dateFallbackEnabled"] = "true";
    config["dateFallbackOrder"] = NormalizeFallbackOrder(null);
    config["dateMissingMode"] = "empty";
    config["dateMissingText"] = "";
    config["realTimePreview"] = "true";
    config["checksumAlgorithm"] = "CRC32";
    config["checksumDigits"] = "8";
    config["checksumPrefix"] = "[";
    config["checksumSuffix"] = "]";
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

public static bool GetConfigBool(Dictionary<string, string> config, string key, bool defaultValue)
{
    string value = GetConfigString(config, key, null);
    if (value == null)
        return defaultValue;
    value = value.Trim().ToLowerInvariant();
    if (value == "true" || value == "1" || value == "yes" || value == "是")
        return true;
    if (value == "false" || value == "0" || value == "no" || value == "否")
        return false;
    bool result;
    if (bool.TryParse(value, out result))
        return result;
    return defaultValue;
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
    config["dateFallbackEnabled"] = GetConfigBool(config, "dateFallbackEnabled", true) ? "true" : "false";
    config["dateFallbackOrder"] = NormalizeFallbackOrder(GetConfigString(config, "dateFallbackOrder", ""));
    string missingMode = (GetConfigString(config, "dateMissingMode", "empty") ?? "").Trim().ToLowerInvariant();
    config["dateMissingMode"] = missingMode == "custom" ? "custom" : "empty";
    config["dateMissingText"] = GetConfigString(config, "dateMissingText", "");
    config["realTimePreview"] = GetConfigBool(config, "realTimePreview", true) ? "true" : "false";

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

    config["checksumPrefix"] = GetConfigString(config, "checksumPrefix", "[");
    config["checksumSuffix"] = GetConfigString(config, "checksumSuffix", "]");

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
    preset["presetVersion"] = "0.02";
    preset["rootMode"] = normalized["rootMode"];
    preset["rootPath"] = normalized["rootPath"];
    preset["startYear"] = normalized["startYear"];
    preset["dateSource"] = normalized["dateSource"];
    preset["dateFallbackEnabled"] = normalized["dateFallbackEnabled"];
    preset["dateFallbackOrder"] = normalized["dateFallbackOrder"];
    preset["dateMissingMode"] = normalized["dateMissingMode"];
    preset["dateMissingText"] = normalized["dateMissingText"];
    preset["realTimePreview"] = normalized["realTimePreview"];
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
        "rootMode", "rootPath", "startYear", "dateSource",
        "dateFallbackEnabled", "dateFallbackOrder", "dateMissingMode", "dateMissingText",
        "realTimePreview", "checksumAlgorithm", "checksumDigits",
        "checksumPrefix", "checksumSuffix", "template"
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
