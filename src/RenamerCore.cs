using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ============================================================================
// 批量重命名器核心逻辑（Quicker 辅助 C# 代码片段）
// 版本：0.06.21
// 说明：本文件不包含 namespace / class 声明，部署时会与 CustomWindow.Aux.cs
//       合并后放入 Quicker【自定义窗口】的【辅助C#代码】中。
// 兼容性：按 C# 5.0 / .NET Framework 4.x 语法编写。
// ============================================================================

public const string DefaultTemplate = "【根路径】/【日期yyyy】.【日期mm】/aaa/bbb/【源文件名】-【校验码】.【扩展名】";
public const string RenameDefaultTemplate = "【根路径】/【源文件名】-【校验码】.【扩展名】";

public static readonly string[] SupportedAlgorithms = new string[] { "CRC32", "BLAKE3", "MD5", "SHA-256" };
public static readonly string[] SupportedDateSources = new string[] { "filename", "exif", "modified", "created" };
public static readonly string[] PresetConfigKeys = new string[] {
    "rootMode", "rootPath", "startYear",
    "filterExtensions", "filterMode",
    "dateSource", "dateFallbackOrder", "dateFallbackCustomText",
    "preferExifOnFilenameConflict",
    "mergeAdjacentPathSegments", "mergeRepeatedPathSequences",
    "realTimePreview", "execParallel", "execVerify",
    "checksumAlgorithm", "checksumDigits", "checksumPrefix", "checksumSuffix",
    "template"
};
public static readonly string[] TemplateTokens = new string[] {
    "【日期yyyy】", "【日期mm】", "【日期dd】", "【根路径】",
    "【源文件名】", "【扩展名】", "【校验码】"
};

// 左侧要求非数字边界；右侧允许数字，兼容“日期+序号”紧邻的命名（如202002021）。
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

private const uint Blake3ChunkStart = 1U;
private const uint Blake3ChunkEnd = 2U;
private const uint Blake3Parent = 4U;
private const uint Blake3Root = 8U;

// BLAKE3 压缩与分块使用线程内复用缓冲区，避免每个块/分块分配数组。
[ThreadStatic] private static uint[] _blake3Block;
[ThreadStatic] private static uint[] _blake3LastBlock;
[ThreadStatic] private static uint[] _blake3Cv;
[ThreadStatic] private static uint[] _blake3Output;
[ThreadStatic] private static byte[] _blake3Chunk;
[ThreadStatic] private static byte[] _blake3ReadBuffer;
[ThreadStatic] private static uint[] _blake3Stack;
[ThreadStatic] private static uint[] _blake3CvBatch;
[ThreadStatic] private static byte[] _blake3HashBytes;

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
    string value;
    if (TryNormalizeDateSource(source, out value))
        return value;
    return "filename";
}

public static string NormalizeFallbackOrder(string order)
{
    string[] defaults = new string[] { "filename", "exif", "modified", "created", "empty" };
    string[] parts = order == null ? new string[0] : order.Split(',');
    string[] result = new string[5];
    for (int i = 0; i < result.Length; i++)
    {
        string token = i < parts.Length ? NormalizeFallbackToken(parts[i]) : "";
        result[i] = token.Length == 0 ? defaults[i] : token;
    }
    return string.Join(",", result);
}

public static string NormalizeFallbackToken(string token)
{
    string value = (token == null ? "" : token.Trim()).ToLowerInvariant();
    if (value.Length == 0)
        return "";
    if (value == "empty" || value == "null" || value == "空" || value == "空值")
        return "empty";
    if (value == "custom" || value == "自定义" || value == "自定义值")
        return "custom";
    if (value == "skip" || value == "跳过")
        return "skip";

    string source;
    if (TryNormalizeDateSource(value, out source))
        return source;
    return "";
}

private static bool TryNormalizeDateSource(string value, out string source)
{
    source = "";
    string normalized = (value == null ? "" : value.Trim()).ToLowerInvariant();
    if (normalized == "filename" || normalized == "file" || normalized == "文件名" || normalized == "文件名中的日期")
    {
        source = "filename";
        return true;
    }
    if (normalized == "exif" || normalized == "exif拍摄日期" || normalized == "拍摄日期")
    {
        source = "exif";
        return true;
    }
    if (normalized == "modified" || normalized == "修改" || normalized == "修改日期")
    {
        source = "modified";
        return true;
    }
    if (normalized == "created" || normalized == "创建" || normalized == "创建日期")
    {
        source = "created";
        return true;
    }
    return false;
}

public static void ResolveDateValue(IDictionary<string, DateTime?> dates,
    string primarySource, string fallbackOrder, string customText,
    out DateTime? effectiveDate, out string effectiveSource,
    out string missingText, out bool skipped)
{
    ResolveDateValue(dates, primarySource, fallbackOrder, customText, false,
        out effectiveDate, out effectiveSource, out missingText, out skipped);
}

public static void ResolveDateValue(IDictionary<string, DateTime?> dates,
    string primarySource, string fallbackOrder, string customText,
    bool preferExifOnFilenameConflict,
    out DateTime? effectiveDate, out string effectiveSource,
    out string missingText, out bool skipped)
{
    if (dates == null)
    {
        effectiveDate = null;
        effectiveSource = "empty";
        missingText = "";
        skipped = false;
        return;
    }

    DateTime? filenameDate;
    DateTime? exifDate;
    DateTime? modifiedDate;
    DateTime? createdDate;
    dates.TryGetValue("filename", out filenameDate);
    dates.TryGetValue("exif", out exifDate);
    dates.TryGetValue("modified", out modifiedDate);
    dates.TryGetValue("created", out createdDate);

    ResolveDateValue(filenameDate, exifDate, modifiedDate, createdDate,
        primarySource, fallbackOrder, customText, preferExifOnFilenameConflict,
        out effectiveDate, out effectiveSource, out missingText, out skipped);
}

// 四值重载：批量刷新日期时避免为每一行分配 Dictionary。
public static void ResolveDateValue(DateTime? filenameDate, DateTime? exifDate,
    DateTime? modifiedDate, DateTime? createdDate,
    string primarySource, string fallbackOrder, string customText,
    bool preferExifOnFilenameConflict,
    out DateTime? effectiveDate, out string effectiveSource,
    out string missingText, out bool skipped)
{
    effectiveDate = null;
    effectiveSource = "";
    missingText = "";
    skipped = false;

    string primary = NormalizeDateSource(primarySource);
    bool hasFilenameExifConflict = preferExifOnFilenameConflict &&
        filenameDate.HasValue && exifDate.HasValue &&
        filenameDate.Value.Date != exifDate.Value.Date;

    DateTime? value = GetDateBySource(primary, filenameDate, exifDate, modifiedDate, createdDate);
    if (value.HasValue)
    {
        if (primary == "filename" && hasFilenameExifConflict)
        {
            effectiveDate = exifDate;
            effectiveSource = "exif";
            return;
        }
        effectiveDate = value;
        effectiveSource = primary;
        return;
    }

    string[] order = NormalizeFallbackOrder(fallbackOrder).Split(',');
    for (int i = 0; i < order.Length; i++)
    {
        string token = order[i];
        if (token == "empty")
        {
            effectiveSource = "empty";
            return;
        }
        if (token == "custom")
        {
            effectiveSource = "custom";
            missingText = customText == null ? "" : customText;
            return;
        }
        if (token == "skip")
        {
            effectiveSource = "skip";
            skipped = true;
            return;
        }
        value = GetDateBySource(token, filenameDate, exifDate, modifiedDate, createdDate);
        if (value.HasValue)
        {
            if (token == "filename" && hasFilenameExifConflict)
            {
                effectiveDate = exifDate;
                effectiveSource = "exif";
                return;
            }
            effectiveDate = value;
            effectiveSource = token;
            return;
        }
    }

    effectiveSource = "empty";
}

private static DateTime? GetDateBySource(string source, DateTime? filenameDate,
    DateTime? exifDate, DateTime? modifiedDate, DateTime? createdDate)
{
    if (source == "filename") return filenameDate;
    if (source == "exif") return exifDate;
    if (source == "modified") return modifiedDate;
    if (source == "created") return createdDate;
    return null;
}

public static string NormalizeExtensionList(string value)
{
    if (value == null)
        return "";
    string[] parts = value.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n', '|' });
    List<string> result = new List<string>();
    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < parts.Length; i++)
    {
        string part = (parts[i] == null ? "" : parts[i].Trim());
        if (part.Length == 0)
            continue;
        if (part == "*" || part == "*.*" || part.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (seen.Add("*"))
                result.Add("*");
            continue;
        }
        part = part.TrimStart('*').TrimStart('.').Trim().ToLowerInvariant();
        if (part.Length == 0)
            continue;
        if (seen.Add(part))
            result.Add(part);
    }
    return string.Join(";", result.ToArray());
}

public static string NormalizeFilterMode(string mode)
{
    string value = (mode == null ? "" : mode.Trim()).ToLowerInvariant();
    if (value == "copy" || value == "复制")
        return "copy";
    if (value == "move" || value == "移动")
        return "move";
    if (value == "rename" || value == "重命名")
        return "rename";
    // 旧版 execute/执行/白名单表示按模板处理，迁移为 rename；目标目录变化时动作会自动变为复制。
    if (value == "execute" || value == "执行" || value == "白名单" || value == "include")
        return "rename";
    return "skip";
}

public static string GetDefaultTemplateForMode(string mode)
{
    return NormalizeFilterMode(mode) == "rename" ? RenameDefaultTemplate : DefaultTemplate;
}

/// <summary>
/// 根据决策模式、过滤器命中和目标目录是否变化，决定每个文件在展示界面中的动作。
/// 跳过模式是黑名单：命中列表的文件跳过，其他文件按模板执行（同目录为重命名，跨目录为复制）。
/// 复制/移动/重命名模式是白名单：仅命中列表的文件执行，其他文件跳过；空列表表示不过滤。
/// </summary>
public static string ResolveAction(string mode, bool extensionMatched, bool hasFilterList, bool sameDirectory)
{
    string normalized = NormalizeFilterMode(mode);
    string fallback = sameDirectory ? "重命名" : "复制";

    if (normalized == "skip")
    {
        if (!hasFilterList)
            return fallback;
        return extensionMatched ? "跳过" : fallback;
    }

    if (hasFilterList && !extensionMatched)
        return "跳过";

    if (normalized == "copy")
        return "复制";
    if (normalized == "move")
        return "移动";
    return sameDirectory ? "重命名" : "复制";
}

public static int NormalizeParallelCount(int value)
{
    if (value < 1) return 1;
    if (value > 8) return 8;
    return value;
}

// ---------------------------------------------------------------------------
// 文件传输引擎：按驱动器类型选择缓冲策略，带进度、重试与失败清理。
// ---------------------------------------------------------------------------

public const int TransferBufferSmall = 64 * 1024;
public const int TransferBufferNetwork = 256 * 1024;
public const int TransferBufferDefault = 1024 * 1024;
public const int TransferBufferRemovableLarge = 2 * 1024 * 1024;
public const int TransferBufferLarge = 4 * 1024 * 1024;

private const uint CopyFileFailIfExists = 0x00000001;

private delegate int CopyProgressRoutine(
    long totalFileSize,
    long totalBytesTransferred,
    long streamSize,
    long streamBytesTransferred,
    uint streamNumber,
    uint callbackReason,
    IntPtr sourceFile,
    IntPtr destinationFile,
    IntPtr data);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern bool CopyFileEx(
    string existingFileName,
    string newFileName,
    CopyProgressRoutine progressRoutine,
    IntPtr data,
    ref int cancel,
    uint copyFlags);

public static bool IsNetworkTransferPath(string path)
{
    if (path == null)
        return false;
    if (path.StartsWith("\\\\", StringComparison.Ordinal))
        return true;
    try
    {
        string root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;
        return new DriveInfo(root).DriveType == DriveType.Network;
    }
    catch
    {
        return false;
    }
}

public static DriveType GetDriveTypeSafe(string path)
{
    if (IsNetworkTransferPath(path))
        return DriveType.Network;
    try
    {
        string root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return DriveType.Unknown;
        return new DriveInfo(root).DriveType;
    }
    catch
    {
        return DriveType.Unknown;
    }
}

public static int SelectCopyBufferSize(string path, long fileSize)
{
    if (IsNetworkTransferPath(path))
        return TransferBufferNetwork;
    return SelectCopyBufferSizeForDrive(GetDriveTypeSafe(path), fileSize);
}

public static int SelectCopyBufferSizeForDrive(DriveType drive, long fileSize)
{
    if (drive == DriveType.Network)
        return TransferBufferNetwork;
    if (drive == DriveType.CDRom)
        return TransferBufferSmall;
    if (drive == DriveType.Removable)
        return fileSize >= 512L * 1024 * 1024 ? TransferBufferRemovableLarge : TransferBufferDefault;
    if (drive == DriveType.Fixed || drive == DriveType.Unknown)
        return fileSize >= 1024L * 1024 * 1024 ? TransferBufferLarge : TransferBufferDefault;
    return TransferBufferDefault;
}

public static string DescribeTransferProfile(string path, long fileSize)
{
    DriveType drive = GetDriveTypeSafe(path);
    return drive.ToString().ToLowerInvariant() + ":" + SelectCopyBufferSize(path, fileSize).ToString(CultureInfo.InvariantCulture);
}

// 根据样本量与CPU占用判断性能瓶颈；阈值与界面提示保持一致。
public static string ClassifyPerformanceBottleneck(int count, double seconds, double cpuRatio)
{
    if (count < 64 || seconds < 0.05)
        return "样本小";
    if (cpuRatio < 0)
        cpuRatio = 0;
    if (cpuRatio > 1)
        cpuRatio = 1;
    if (cpuRatio >= 0.65)
        return "CPU";
    if (cpuRatio <= 0.30)
        return "I/O";
    return "CPU/I/O混合";
}

// 默认冲突比较校验码：CRC32 速度快，最终仍以逐字节比较确认。
public const string ConflictCompareAlgorithm = "CRC32";

[ThreadStatic] private static byte[] _compareBufferA;
[ThreadStatic] private static byte[] _compareBufferB;

// 在文件名（不含扩展名）后追加 " (n)"，从1开始由调用方递增。
public static string BuildNumberedSuffixPath(string path, int number)
{
    if (path == null || path.Length == 0 || number < 1)
        return path;
    string directory;
    try { directory = Path.GetDirectoryName(path); }
    catch { directory = null; }
    string fileName = Path.GetFileNameWithoutExtension(path);
    string extension = Path.GetExtension(path);
    string suffixed = (fileName == null ? "" : fileName) + " (" +
        number.ToString(CultureInfo.InvariantCulture) + ")" +
        (extension == null ? "" : extension);
    if (directory == null || directory.Length == 0)
        return suffixed;
    return Path.Combine(directory, suffixed);
}

// 判断目标文件是否与源文件内容相同：大小 -> 默认校验码 -> 逐字节。
// 云端占位文件不读取内容，避免因此触发下载。
public static bool TryCompareFileContent(string source, string target,
    out bool identical, out bool cloudPlaceholder, out string error)
{
    identical = false;
    cloudPlaceholder = false;
    error = null;
    if (source == null || source.Length == 0 || target == null || target.Length == 0)
    {
        error = "路径为空";
        return false;
    }

    try
    {
        FileInfo sourceInfo = new FileInfo(source);
        FileInfo targetInfo = new FileInfo(target);
        if (!sourceInfo.Exists)
        {
            error = "源文件不存在";
            return false;
        }
        if (!targetInfo.Exists)
        {
            error = "目标文件不存在";
            return false;
        }

        int sourceAttributes = 0;
        int targetAttributes = 0;
        try { sourceAttributes = (int)sourceInfo.Attributes; } catch { }
        try { targetAttributes = (int)targetInfo.Attributes; } catch { }
        if (IsCloudPlaceholderAttributes(sourceAttributes) ||
            IsCloudPlaceholderAttributes(targetAttributes))
        {
            cloudPlaceholder = true;
            error = "云端占位文件不读取内容";
            return false;
        }

        if (sourceInfo.Length != targetInfo.Length)
        {
            identical = false;
            return true;
        }

        string hashError;
        string sourceHash = ComputeHashHex(source, ConflictCompareAlgorithm, out hashError);
        if (sourceHash == null)
        {
            error = "源文件校验码计算失败：" + (hashError == null ? "未知错误" : hashError);
            return false;
        }
        string targetHash = ComputeHashHex(target, ConflictCompareAlgorithm, out hashError);
        if (targetHash == null)
        {
            error = "目标文件校验码计算失败：" + (hashError == null ? "未知错误" : hashError);
            return false;
        }
        if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
        {
            identical = false;
            return true;
        }

        return FilesAreBinaryEqual(source, target, out identical, out error);
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

// 逐字节比较两个本地文件；调用前应已完成大小与校验码预筛。
public static bool FilesAreBinaryEqual(string first, string second,
    out bool equal, out string error)
{
    equal = false;
    error = null;
    try
    {
        if (_compareBufferA == null)
            _compareBufferA = new byte[81920];
        if (_compareBufferB == null)
            _compareBufferB = new byte[81920];

        using (FileStream left = new FileStream(first, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
        using (FileStream right = new FileStream(second, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 81920, FileOptions.SequentialScan))
        {
            if (left.Length != right.Length)
                return true;

            while (true)
            {
                int leftRead = ReadBlock(left, _compareBufferA);
                int rightRead = ReadBlock(right, _compareBufferB);
                if (leftRead != rightRead)
                    return true;
                if (leftRead == 0)
                {
                    equal = true;
                    return true;
                }
                for (int i = 0; i < leftRead; i++)
                {
                    if (_compareBufferA[i] != _compareBufferB[i])
                        return true;
                }
            }
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

private static int ReadBlock(Stream stream, byte[] buffer)
{
    int total = 0;
    while (total < buffer.Length)
    {
        int read = stream.Read(buffer, total, buffer.Length - total);
        if (read <= 0)
            break;
        total += read;
    }
    return total;
}

// Windows 云筛选器占位属性：内容不在本地，打开文件会触发下载。
public const int FileAttributeOffline = 0x00001000;
public const int FileAttributeRecallOnOpen = 0x00040000;
public const int FileAttributeRecallOnDataAccess = 0x00400000;

public static bool IsCloudPlaceholderAttributes(int fileAttributes)
{
    return (fileAttributes & FileAttributeOffline) != 0 ||
        (fileAttributes & FileAttributeRecallOnOpen) != 0 ||
        (fileAttributes & FileAttributeRecallOnDataAccess) != 0;
}

public static bool IsSameVolumePath(string source, string target)
{
    return string.Equals(GetVolumeRoot(source), GetVolumeRoot(target),
        StringComparison.OrdinalIgnoreCase);
}

// 显式识别盘符与UNC共享；避免非Windows宿主上Path.GetPathRoot返回空串导致误判。
private static string GetVolumeRoot(string path)
{
    if (path == null)
        return "";
    string value = path.Trim();
    if (value.Length >= 2 &&
        ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z')) &&
        value[1] == ':')
    {
        return char.ToUpperInvariant(value[0]).ToString() + ":";
    }
    if (value.Length >= 2 &&
        ((value[0] == '\\' && value[1] == '\\') || (value[0] == '/' && value[1] == '/')))
    {
        int serverEnd = value.IndexOfAny(new char[] { '\\', '/' }, 2);
        if (serverEnd < 0)
            return value.ToUpperInvariant();
        int shareEnd = value.IndexOfAny(new char[] { '\\', '/' }, serverEnd + 1);
        string server = value.Substring(2, serverEnd - 2);
        string share = shareEnd < 0
            ? value.Substring(serverEnd + 1)
            : value.Substring(serverEnd + 1, shareEnd - serverEnd - 1);
        return ("\\\\" + server + "\\" + share).ToUpperInvariant();
    }
    try
    {
        string root = Path.GetPathRoot(value);
        return root == null ? "" : root;
    }
    catch
    {
        return "";
    }
}

public static bool IsFileUnchanged(string path, long expectedLength, DateTime expectedLastWriteUtc)
{
    try
    {
        FileInfo info = new FileInfo(path);
        return info.Exists && info.Length == expectedLength &&
            info.LastWriteTimeUtc == expectedLastWriteUtc;
    }
    catch
    {
        return false;
    }
}

public static bool CheckFreeSpace(string targetPath, long requiredBytes, out string error)
{
    error = null;
    if (requiredBytes <= 0)
        return true;
    if (IsNetworkTransferPath(targetPath))
        return true;

    try
    {
        string root = Path.GetPathRoot(targetPath);
        if (string.IsNullOrEmpty(root))
            return true;
        DriveInfo drive = new DriveInfo(root);
        if (drive.DriveType == DriveType.Network || drive.DriveType == DriveType.NoRootDirectory)
            return true;
        long available = drive.AvailableFreeSpace;
        if (available <= 0)
            return true;
        long reserve = Math.Max(32L * 1024 * 1024, requiredBytes / 100);
        if (available < reserve || requiredBytes > available - reserve)
        {
            long requiredMb = (requiredBytes + 1024L * 1024 - 1) / (1024L * 1024);
            if (requiredMb < 1)
                requiredMb = 1;
            error = "目标磁盘空间不足：需要约 " + requiredMb.ToString(CultureInfo.InvariantCulture) +
                " MB，可用 " + (available / 1024 / 1024).ToString(CultureInfo.InvariantCulture) + " MB";
            return false;
        }
    }
    catch
    {
        return true;
    }
    return true;
}

public static int GetTransferRetryDelayMs(int attempt)
{
    if (attempt < 1) attempt = 1;
    int delay = 200;
    for (int i = 1; i < attempt && delay < 2000; i++)
        delay *= 2;
    return delay > 2000 ? 2000 : delay;
}

public static bool IsTransientTransferError(int errorCode)
{
    switch (errorCode)
    {
        case 5:
        case 19:
        case 21:
        case 32:
        case 33:
        case 53:
        case 64:
        case 67:
        case 121:
        case 1231:
            return true;
        default:
            return false;
    }
}

public static string DescribeTransferError(int errorCode)
{
    switch (errorCode)
    {
        case 2: return "源文件不存在";
        case 3: return "路径不存在";
        case 5: return "拒绝访问";
        case 19: return "设备未就绪";
        case 21: return "设备未就绪";
        case 32: return "文件被其他程序占用";
        case 33: return "文件被锁定";
        case 39: return "磁盘空间不足";
        case 53: return "网络路径不可用";
        case 64: return "网络连接已断开";
        case 67: return "找不到网络路径";
        case 80: return "目标文件已存在";
        case 112: return "磁盘空间不足";
        case 121: return "网络传输超时";
        case 123: return "文件名语法错误";
        case 183: return "目标文件已存在";
        case 206: return "文件名或路径过长";
        case 1231: return "网络位置不可用";
        case 1235: return "操作已取消";
        default: return "文件操作失败（系统错误 " + errorCode.ToString(CultureInfo.InvariantCulture) + "）";
    }
}

public static bool CopyFileAdaptive(string source, string target,
    Action<long> progress, Func<bool> cancel, out string error, out int errorCode)
{
    error = null;
    errorCode = 0;

    if (!File.Exists(source))
    {
        error = "源文件不存在";
        errorCode = 2;
        return false;
    }
    if (File.Exists(target))
    {
        error = "目标文件已存在";
        errorCode = 80;
        return false;
    }
    if (cancel != null && cancel())
    {
        error = "已取消";
        return false;
    }

    long length;
    DateTime lastWriteUtc;
    try
    {
        FileInfo info = new FileInfo(source);
        length = info.Length;
        lastWriteUtc = info.LastWriteTimeUtc;
    }
    catch (Exception ex)
    {
        errorCode = GetTransferErrorCode(ex);
        error = DescribeTransferException(ex, errorCode);
        return false;
    }

    if (!CheckFreeSpace(target, length, out error))
    {
        errorCode = 39;
        return false;
    }

    string temp = BuildTransferTempPath(target);
    bool copied = false;
    try
    {
        DriveType drive = GetDriveTypeSafe(target);
        int bufferSize = SelectCopyBufferSize(target, length);
        bool preallocate = drive == DriveType.Fixed && length >= 64L * 1024 * 1024;
        bool flushToDisk = drive == DriveType.Removable;

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            string nativeError;
            int nativeCode;
            if (TryCopyFileNative(source, temp, progress, cancel, out nativeError, out nativeCode))
            {
                copied = true;
            }
            else
            {
                error = nativeError;
                errorCode = nativeCode;
                TryDeleteFile(temp);
                if (cancel != null && cancel())
                    return false;
                if (!IsNativeFallbackError(nativeCode))
                    return false;
            }
        }

        if (!copied)
        {
            string managedError;
            int managedCode;
            if (!CopyFileManaged(source, temp, length, bufferSize, preallocate, flushToDisk,
                progress, cancel, out managedError, out managedCode))
            {
                error = managedError;
                errorCode = managedCode;
                return false;
            }
        }

        if (!IsFileUnchanged(source, length, lastWriteUtc))
        {
            error = "源文件在复制期间发生变化";
            return false;
        }
        if (cancel != null && cancel())
        {
            error = "已取消";
            return false;
        }
        if (File.Exists(target))
        {
            error = "目标文件已存在";
            errorCode = 80;
            return false;
        }

        File.Move(temp, target);
        return true;
    }
    catch (Exception ex)
    {
        errorCode = GetTransferErrorCode(ex);
        error = DescribeTransferException(ex, errorCode);
        return false;
    }
    finally
    {
        TryDeleteFile(temp);
    }
}

public static bool CopyFileWithRetry(string source, string target,
    Action<long> progress, Func<bool> cancel, int attempts, out string error)
{
    error = null;
    if (attempts < 1)
        attempts = 1;

    for (int attempt = 1; attempt <= attempts; attempt++)
    {
        if (cancel != null && cancel())
        {
            error = "已取消";
            return false;
        }

        long attemptBytes = 0;
        Action<long> attemptProgress = null;
        if (progress != null)
        {
            attemptProgress = delegate(long delta)
            {
                attemptBytes += delta;
                progress(delta);
            };
        }

        string attemptError;
        int attemptCode;
        bool ok = CopyFileAdaptive(source, target, attemptProgress, cancel,
            out attemptError, out attemptCode);

        if (ok)
            return true;

        if (attemptBytes != 0 && progress != null)
            progress(-attemptBytes);

        error = attemptError;
        if (cancel != null && cancel())
            return false;
        if (!IsTransientTransferError(attemptCode) || attempt >= attempts)
            return false;

        Thread.Sleep(GetTransferRetryDelayMs(attempt));
    }
    return false;
}

public static bool MoveFileWithRetry(string source, string target, int attempts, out string error)
{
    error = null;
    if (attempts < 1)
        attempts = 1;
    if (source == null || source.Length == 0)
    {
        error = "源路径为空";
        return false;
    }
    if (target == null || target.Length == 0)
    {
        error = "目标路径为空";
        return false;
    }
    // 预检避免把“目标已存在”暴露成系统错误码 183。
    if (!File.Exists(source))
    {
        error = "源文件不存在";
        return false;
    }
    if (File.Exists(target))
    {
        error = "目标文件已存在";
        return false;
    }

    for (int attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            File.Move(source, target);
            return true;
        }
        catch (Exception ex)
        {
            int code = GetTransferErrorCode(ex);
            error = DescribeTransferException(ex, code);
            if (!IsTransientTransferError(code) || attempt >= attempts)
                return false;
            Thread.Sleep(GetTransferRetryDelayMs(attempt));
        }
    }
    return false;
}

private static bool TryCopyFileNative(string source, string target,
    Action<long> progress, Func<bool> cancel, out string error, out int errorCode)
{
    error = null;
    errorCode = 0;
    if (Environment.OSVersion.Platform != PlatformID.Win32NT)
        return false;

    long reported = 0;
    CopyProgressRoutine routine = delegate(long totalFileSize, long totalBytesTransferred,
        long streamSize, long streamBytesTransferred, uint streamNumber, uint callbackReason,
        IntPtr sourceFile, IntPtr destinationFile, IntPtr data)
    {
        if (totalBytesTransferred > reported)
        {
            long delta = totalBytesTransferred - reported;
            reported = totalBytesTransferred;
            if (progress != null)
                progress(delta);
        }
        if (cancel != null && cancel())
            return 1;
        return 0;
    };

    int cancelFlag = 0;
    bool ok = CopyFileEx(source, target, routine, IntPtr.Zero, ref cancelFlag, CopyFileFailIfExists);
    if (ok)
    {
        try
        {
            long length = new FileInfo(source).Length;
            if (length > reported && progress != null)
                progress(length - reported);
        }
        catch
        {
        }
        return true;
    }

    errorCode = Marshal.GetLastWin32Error();
    error = DescribeTransferError(errorCode);
    return false;
}

private static bool CopyFileManaged(string source, string target, long length,
    int bufferSize, bool preallocate, bool flushToDisk, Action<long> progress,
    Func<bool> cancel, out string error, out int errorCode)
{
    error = null;
    errorCode = 0;
    FileStream input = null;
    FileStream output = null;
    try
    {
        input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.SequentialScan);

        if (preallocate)
        {
            try { output.SetLength(length); }
            catch { }
        }

        byte[] buffer = new byte[bufferSize];
        while (true)
        {
            if (cancel != null && cancel())
            {
                error = "已取消";
                return false;
            }
            int read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;
            output.Write(buffer, 0, read);
            if (progress != null)
                progress(read);
        }

        if (flushToDisk)
            output.Flush(true);
        else
            output.Flush();

        input.Close();
        input = null;
        output.Close();
        output = null;
        CopyFileTimestamps(source, target);
        return true;
    }
    catch (Exception ex)
    {
        errorCode = GetTransferErrorCode(ex);
        error = DescribeTransferException(ex, errorCode);
        return false;
    }
    finally
    {
        if (input != null)
        {
            try { input.Dispose(); }
            catch { }
        }
        if (output != null)
        {
            try { output.Dispose(); }
            catch { }
        }
    }
}

private static void CopyFileTimestamps(string source, string target)
{
    try { File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source)); }
    catch { }
    try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); }
    catch { }
    try { File.SetAttributes(target, File.GetAttributes(source)); }
    catch { }
}

private static string BuildTransferTempPath(string target)
{
    string directory = null;
    try { directory = Path.GetDirectoryName(target); }
    catch { }
    string name = ".qk-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp";
    if (string.IsNullOrEmpty(directory))
        return name;
    return Path.Combine(directory, name);
}

private static bool IsNativeFallbackError(int errorCode)
{
    return errorCode == 1 || errorCode == 50 || errorCode == 120;
}

private static int GetTransferErrorCode(Exception ex)
{
    try
    {
        if (ex is UnauthorizedAccessException)
            return 5;
        if (ex is FileNotFoundException)
            return 2;
        if (ex is DirectoryNotFoundException)
            return 3;
        if (ex is PathTooLongException)
            return 206;
        if (ex is IOException)
        {
            int hr = Marshal.GetHRForException(ex);
            int code = hr & 0xFFFF;
            if (code != 0)
                return code;
        }
    }
    catch
    {
    }
    return 0;
}

private static string DescribeTransferException(Exception ex, int errorCode)
{
    if (errorCode != 0)
        return DescribeTransferError(errorCode);
    return ex == null || ex.Message == null || ex.Message.Length == 0
        ? "文件操作失败" : ex.Message;
}

private static void TryDeleteFile(string path)
{
    if (path == null)
        return;
    try
    {
        if (File.Exists(path))
        {
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch { }
            File.Delete(path);
        }
    }
    catch
    {
    }
}

public static bool ShouldSkipByFilter(string extension, string extensions, string mode)
{
    string list = NormalizeExtensionList(extensions);
    bool hasFilter = list.Length > 0;
    bool matched = hasFilter && ExtensionMatchesFilter(extension, list);
    return ResolveAction(mode, matched, hasFilter, true) == "跳过";
}

public static bool ExtensionMatchesFilter(string extension, string normalizedExtensions)
{
    if (normalizedExtensions == null || normalizedExtensions.Length == 0)
        return false;

    string normalized = (extension == null ? "" : extension.Trim().TrimStart('.')).ToLowerInvariant();
    string[] items = normalizedExtensions.Split(';');
    for (int i = 0; i < items.Length; i++)
    {
        if (items[i] == "*" || items[i] == normalized)
            return true;
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

// 位数统一钳制：最低1位，最高为算法摘要长度；未知算法按1位处理。
public static int ClampChecksumDigits(string algorithm, int digits)
{
    int maxDigits = GetAlgorithmMaxDigits(algorithm);
    if (maxDigits <= 0) maxDigits = 1;
    if (digits < 1) return 1;
    if (digits > maxDigits) return maxDigits;
    return digits;
}

public static string GetAlgorithmLabel(string algorithm, int digits)
{
    string value = NormalizeAlgorithm(algorithm);
    int maxDigits = GetAlgorithmMaxDigits(value);
    if (maxDigits <= 0)
        return value;
    int actualDigits = ClampChecksumDigits(value, digits);
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
    int actualDigits = ClampChecksumDigits(value, digits);
    return ComposeChecksumBadge(value, actualDigits, prefix, suffix, TruncateHash(hex, actualDigits));
}

public static string BuildPendingChecksumBadge(string algorithm, int digits, string prefix, string suffix)
{
    string value = NormalizeAlgorithm(algorithm);
    int actualDigits = ClampChecksumDigits(value, digits);
    return ComposeChecksumBadge(value, actualDigits, prefix, suffix, "…");
}

private static string ComposeChecksumBadge(string algorithm, int digits, string prefix, string suffix, string code)
{
    string left = prefix == null ? "" : prefix;
    string right = suffix == null ? "" : suffix;
    return left + GetAlgorithmLabel(algorithm, digits) + "-" + code + right;
}

public static string ComputeHashHex(string filePath, string algorithm, out string error)
{
    error = null;
    try
    {
        using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            return ComputeHashHexFromStream(stream, algorithm, out error);
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
}

public static string ComputeHashHexFromStream(Stream stream, string algorithm, out string error)
{
    error = null;
    string value = NormalizeAlgorithm(algorithm);
    if (!IsSupportedAlgorithm(value))
    {
        error = "不支持的校验算法：" + (algorithm == null ? "" : algorithm);
        return null;
    }
    if (stream == null)
    {
        error = "哈希输入流为空";
        return null;
    }

    try
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
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
}

public static string ComputeHashHexFromBytes(byte[] data, string algorithm, out string error)
{
    error = null;
    using (MemoryStream stream = new MemoryStream(data == null ? new byte[0] : data))
    {
        return ComputeHashHexFromStream(stream, algorithm, out error);
    }
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
// 说明：.NET Framework 4.8 的托管代码无法直接发射 AVX512 指令，这里用
// IsProcessorFeaturePresent 检测 AVX/AVX2/AVX512F，并按检测结果选择分块并
// 行度与读缓冲大小；压缩核心使用局部变量展开，避免数组状态与逐轮置换开销。
// ---------------------------------------------------------------------------

private static readonly object Blake3ProfileLock = new object();
private static volatile int _blake3ProfileReady = 0;
private static int _blake3InstructionLevel = 0;
private static int _blake3Parallelism = 1;
private static int _blake3ReadBufferSize = 128 * 1024;

private const uint ProcessorFeatureAvxInstructions = 39;
private const uint ProcessorFeatureAvx2Instructions = 40;
private const uint ProcessorFeatureAvx512FInstructions = 41;

[DllImport("kernel32.dll")]
private static extern bool IsProcessorFeaturePresent(uint processorFeature);

// 检测结果只用于调度与缓冲策略；压缩核心仍是可移植的托管实现。
private static void EnsureBlake3Profile()
{
    if (_blake3ProfileReady != 0)
        return;
    lock (Blake3ProfileLock)
    {
        if (_blake3ProfileReady != 0)
            return;

        int level = 0;
        try
        {
            if (IsProcessorFeaturePresent(ProcessorFeatureAvx512FInstructions))
                level = 3;
            else if (IsProcessorFeaturePresent(ProcessorFeatureAvx2Instructions))
                level = 2;
            else if (IsProcessorFeaturePresent(ProcessorFeatureAvxInstructions))
                level = 1;
        }
        catch
        {
            level = 0;
        }

        int cores = Environment.ProcessorCount;
        if (cores < 1)
            cores = 1;
        int parallelism = 1;
        int bufferSize = 128 * 1024;
        if (level >= 3)
        {
            parallelism = Math.Min(8, cores);
            bufferSize = 1024 * 1024;
        }
        else if (level == 2)
        {
            parallelism = Math.Min(6, cores);
            bufferSize = 512 * 1024;
        }
        else if (level == 1)
        {
            parallelism = Math.Min(4, cores);
            bufferSize = 256 * 1024;
        }
        if (parallelism < 2)
            parallelism = 1;

        _blake3InstructionLevel = level;
        _blake3Parallelism = parallelism;
        _blake3ReadBufferSize = bufferSize;
        _blake3ProfileReady = 1;
    }
}

public static string GetBlake3AccelerationSummary()
{
    EnsureBlake3Profile();
    string levelText;
    if (_blake3InstructionLevel >= 3)
        levelText = "AVX512F调度";
    else if (_blake3InstructionLevel == 2)
        levelText = "AVX2调度";
    else if (_blake3InstructionLevel == 1)
        levelText = "AVX调度";
    else
        levelText = "标量调度";
    return levelText + "，" + _blake3Parallelism.ToString(CultureInfo.InvariantCulture) +
        "路分块并行，" + (_blake3ReadBufferSize / 1024).ToString(CultureInfo.InvariantCulture) +
        " KB缓冲";
}

private static void EnsureBlake3Scratch()
{
    if (_blake3Block != null)
        return;
    _blake3Block = new uint[16];
    _blake3LastBlock = new uint[16];
    _blake3Cv = new uint[8];
    _blake3Output = new uint[8];
    _blake3Chunk = new byte[1024];
    _blake3HashBytes = new byte[32];
    _blake3Stack = new uint[64 * 8];
}

private static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
{
    int total = 0;
    while (total < count)
    {
        int read = stream.Read(buffer, offset + total, count - total);
        if (read <= 0)
            break;
        total += read;
    }
    return total;
}

private static string ComputeBlake3Hex(Stream stream)
{
    EnsureBlake3Profile();
    EnsureBlake3Scratch();

    int bufferSize = _blake3ReadBufferSize;
    byte[] buffer = _blake3ReadBuffer;
    if (buffer == null || buffer.Length < bufferSize)
    {
        buffer = new byte[bufferSize];
        _blake3ReadBuffer = buffer;
    }

    byte[] carry = _blake3Chunk;
    int carryLength = 0;
    bool carryFull = false;
    uint[] stack = _blake3Stack;
    int stackDepth = 0;
    ulong chunkCounter = 0;

    while (true)
    {
        int read = ReadFull(stream, buffer, 0, buffer.Length);
        bool eof = read < buffer.Length;
        int offset = 0;

        if (carryFull)
        {
            if (read == 0)
                break;
            ProcessChunkRange(carry, 0, 1, chunkCounter, stack, ref stackDepth);
            chunkCounter++;
            carryFull = false;
            carryLength = 0;
        }

        if (carryLength > 0)
        {
            int need = 1024 - carryLength;
            int take = Math.Min(need, read);
            if (take > 0)
            {
                Array.Copy(buffer, 0, carry, carryLength, take);
                carryLength += take;
                offset += take;
            }
            if (carryLength == 1024)
            {
                if (offset < read)
                {
                    ProcessChunkRange(carry, 0, 1, chunkCounter, stack, ref stackDepth);
                    chunkCounter++;
                    carryLength = 0;
                }
                else if (!eof)
                {
                    carryFull = true;
                    continue;
                }
                else
                {
                    break;
                }
            }
        }

        if (carryFull)
            continue;

        int remaining = read - offset;
        if (remaining > 0)
        {
            int fullChunks = remaining / 1024;
            int tail = remaining - fullChunks * 1024;
            if (eof)
            {
                if (tail > 0)
                {
                    ProcessChunkRange(buffer, offset, fullChunks, chunkCounter, stack, ref stackDepth);
                    chunkCounter += (ulong)fullChunks;
                    offset += fullChunks * 1024;
                    Array.Copy(buffer, offset, carry, 0, tail);
                    carryLength = tail;
                    carryFull = false;
                }
                else
                {
                    int nonFinal = fullChunks - 1;
                    if (nonFinal > 0)
                    {
                        ProcessChunkRange(buffer, offset, nonFinal, chunkCounter, stack, ref stackDepth);
                        chunkCounter += (ulong)nonFinal;
                        offset += nonFinal * 1024;
                    }
                    Array.Copy(buffer, offset, carry, 0, 1024);
                    carryLength = 1024;
                    carryFull = true;
                }
                break;
            }

            if (tail > 0)
            {
                ProcessChunkRange(buffer, offset, fullChunks, chunkCounter, stack, ref stackDepth);
                chunkCounter += (ulong)fullChunks;
                offset += fullChunks * 1024;
                Array.Copy(buffer, offset, carry, 0, tail);
                carryLength = tail;
                carryFull = false;
            }
            else if (fullChunks > 0)
            {
                int nonFinal = fullChunks - 1;
                if (nonFinal > 0)
                {
                    ProcessChunkRange(buffer, offset, nonFinal, chunkCounter, stack, ref stackDepth);
                    chunkCounter += (ulong)nonFinal;
                    offset += nonFinal * 1024;
                }
                Array.Copy(buffer, offset, carry, 0, 1024);
                carryLength = 1024;
                carryFull = true;
            }
        }
        else if (eof)
        {
            break;
        }
    }

    int finalLength = carryFull ? 1024 : carryLength;
    return FinalizeBlake3(carry, 0, finalLength, chunkCounter, stack, stackDepth);
}

private static string FinalizeBlake3(byte[] data, int offset, int length,
    ulong chunkCounter, uint[] stack, int stackDepth)
{
    uint[] inputCv;
    uint[] lastBlock;
    uint lastBlockLength;
    uint lastFlags;
    Blake3ChunkOutput(data, offset, length, chunkCounter, out inputCv, out lastBlock,
        out lastBlockLength, out lastFlags);

    uint[] output = _blake3Output;
    uint[] block = _blake3Block;
    ulong outputCounter = chunkCounter;
    while (stackDepth > 0)
    {
        Blake3Compress(inputCv, lastBlock, outputCounter, lastBlockLength, lastFlags, output);
        stackDepth--;
        int leftOffset = stackDepth * 8;
        for (int i = 0; i < 8; i++)
        {
            block[i] = stack[leftOffset + i];
            block[i + 8] = output[i];
        }
        inputCv = Blake3Iv;
        lastBlock = block;
        lastBlockLength = 64;
        lastFlags = Blake3Parent;
        outputCounter = 0;
    }

    Blake3Compress(inputCv, lastBlock, 0, lastBlockLength, lastFlags | Blake3Root, output);
    byte[] hash = _blake3HashBytes;
    for (int i = 0; i < 8; i++)
    {
        uint word = output[i];
        hash[i * 4] = (byte)(word & 0xFFU);
        hash[i * 4 + 1] = (byte)((word >> 8) & 0xFFU);
        hash[i * 4 + 2] = (byte)((word >> 16) & 0xFFU);
        hash[i * 4 + 3] = (byte)((word >> 24) & 0xFFU);
    }
    return ToHex(hash);
}

private static void ProcessChunkRange(byte[] data, int offset, int chunkCount,
    ulong baseCounter, uint[] stack, ref int stackDepth)
{
    if (chunkCount <= 0)
        return;
    if (_blake3Parallelism > 1 && chunkCount >= 64)
    {
        int cvWords = chunkCount * 8;
        uint[] cvs = _blake3CvBatch;
        if (cvs == null || cvs.Length < cvWords)
        {
            cvs = new uint[cvWords];
            _blake3CvBatch = cvs;
        }
        ParallelOptions options = new ParallelOptions();
        options.MaxDegreeOfParallelism = _blake3Parallelism;
        Parallel.For(0, chunkCount, options, delegate(int i)
        {
            Blake3ChunkCvTo(data, offset + i * 1024, 1024,
                baseCounter + (ulong)i, cvs, i * 8);
        });
        for (int i = 0; i < chunkCount; i++)
        {
            Blake3AddChunkCvToStack(cvs, i * 8, baseCounter + (ulong)i + 1UL,
                stack, ref stackDepth);
        }
        return;
    }

    for (int i = 0; i < chunkCount; i++)
    {
        Blake3ChunkCvTo(data, offset + i * 1024, 1024,
            baseCounter + (ulong)i, _blake3Output, 0);
        Blake3AddChunkCvToStack(_blake3Output, 0, baseCounter + (ulong)i + 1UL,
            stack, ref stackDepth);
    }
}

// 计算一个完整/最终分块的 CV；输出为 8 个 32 位字。
private static void Blake3ChunkCvTo(byte[] data, int offset, int length,
    ulong chunkCounter, uint[] output, int outputOffset)
{
    EnsureBlake3Scratch();
    uint[] cv = _blake3Cv;
    for (int i = 0; i < 8; i++)
        cv[i] = Blake3Iv[i];

    uint[] block = _blake3Block;
    int blockCount = length <= 0 ? 1 : (length + 63) / 64;
    int blockOffset = 0;
    for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
    {
        int remaining = length - blockOffset;
        if (remaining > 64) remaining = 64;
        if (remaining < 0) remaining = 0;

        LoadBlock(data, offset + blockOffset, remaining, block);
        uint blockFlags = (blockIndex == 0 ? Blake3ChunkStart : 0U);
        if (blockIndex == blockCount - 1)
        {
            blockFlags |= Blake3ChunkEnd;
            Blake3CompressAt(cv, 0, block, chunkCounter, (uint)remaining,
                blockFlags, output, outputOffset);
            return;
        }

        Blake3Compress(cv, block, chunkCounter, (uint)remaining,
            blockFlags, _blake3Output);
        for (int i = 0; i < 8; i++)
            cv[i] = _blake3Output[i];
        blockOffset += remaining;
    }
}

// 返回最终分块的输入 CV 与最后一个未压缩块，供根节点压缩使用。
private static void Blake3ChunkOutput(byte[] data, int offset, int length,
    ulong chunkCounter, out uint[] inputCv, out uint[] lastBlock,
    out uint lastBlockLength, out uint lastFlags)
{
    EnsureBlake3Scratch();
    uint[] cv = _blake3Cv;
    for (int i = 0; i < 8; i++)
        cv[i] = Blake3Iv[i];

    uint[] block = _blake3Block;
    uint[] last = _blake3LastBlock;
    int blockCount = length <= 0 ? 1 : (length + 63) / 64;
    int blockOffset = 0;
    for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
    {
        int remaining = length - blockOffset;
        if (remaining > 64) remaining = 64;
        if (remaining < 0) remaining = 0;

        LoadBlock(data, offset + blockOffset, remaining, block);
        uint blockFlags = (blockIndex == 0 ? Blake3ChunkStart : 0U);
        if (blockIndex == blockCount - 1)
        {
            blockFlags |= Blake3ChunkEnd;
            Array.Copy(block, last, 16);
            inputCv = cv;
            lastBlock = last;
            lastBlockLength = (uint)remaining;
            lastFlags = blockFlags;
            return;
        }

        Blake3Compress(cv, block, chunkCounter, (uint)remaining,
            blockFlags, _blake3Output);
        for (int i = 0; i < 8; i++)
            cv[i] = _blake3Output[i];
        blockOffset += remaining;
    }

    inputCv = cv;
    lastBlock = last;
    lastBlockLength = 0;
    lastFlags = Blake3ChunkStart | Blake3ChunkEnd;
}

private static void Blake3AddChunkCvToStack(uint[] cv, int cvOffset,
    ulong totalChunks, uint[] stack, ref int stackDepth)
{
    while ((totalChunks & 1UL) == 0UL)
    {
        stackDepth--;
        int leftOffset = stackDepth * 8;
        Blake3ParentCvAt(stack, leftOffset, cv, cvOffset, stack, leftOffset);
        cv = stack;
        cvOffset = leftOffset;
        totalChunks >>= 1;
    }
    Array.Copy(cv, cvOffset, stack, stackDepth * 8, 8);
    stackDepth++;
}

private static void Blake3ParentCvAt(uint[] leftCv, int leftOffset,
    uint[] rightCv, int rightOffset, uint[] destination, int destinationOffset)
{
    EnsureBlake3Scratch();
    uint[] block = _blake3Block;
    for (int i = 0; i < 8; i++)
    {
        block[i] = leftCv[leftOffset + i];
        block[i + 8] = rightCv[rightOffset + i];
    }
    Blake3CompressAt(Blake3Iv, 0, block, 0, 64, Blake3Parent,
        destination, destinationOffset);
}

private static void Blake3Compress(uint[] cv, uint[] message, ulong counter,
    uint blockLength, uint flags, uint[] output)
{
    Blake3CompressAt(cv, 0, message, counter, blockLength, flags, output, 0);
}

private static void LoadBlock(byte[] data, int offset, int length, uint[] block)
{
    int fullWords = length / 4;
    for (int i = 0; i < fullWords; i++)
        block[i] = Load32(data, offset + i * 4);
    if (fullWords >= 16)
        return;

    uint tail = 0;
    int tailOffset = fullWords * 4;
    int tailCount = length - tailOffset;
    for (int i = 0; i < tailCount; i++)
        tail |= (uint)data[offset + tailOffset + i] << (i * 8);
    block[fullWords] = tail;
    for (int i = fullWords + 1; i < 16; i++)
        block[i] = 0;
}

private static uint Load32(byte[] data, int offset)
{
    if (BitConverter.IsLittleEndian)
        return BitConverter.ToUInt32(data, offset);
    return (uint)data[offset] |
        ((uint)data[offset + 1] << 8) |
        ((uint)data[offset + 2] << 16) |
        ((uint)data[offset + 3] << 24);
}

private static void Blake3CompressAt(uint[] cv, int cvOffset, uint[] message,
    ulong counter, uint blockLength, uint flags, uint[] output, int outputOffset)
{
    uint s0 = cv[cvOffset + 0];
    uint s1 = cv[cvOffset + 1];
    uint s2 = cv[cvOffset + 2];
    uint s3 = cv[cvOffset + 3];
    uint s4 = cv[cvOffset + 4];
    uint s5 = cv[cvOffset + 5];
    uint s6 = cv[cvOffset + 6];
    uint s7 = cv[cvOffset + 7];
    uint s8 = Blake3Iv[0];
    uint s9 = Blake3Iv[1];
    uint s10 = Blake3Iv[2];
    uint s11 = Blake3Iv[3];
    uint s12 = (uint)(counter & 0xFFFFFFFFUL);
    uint s13 = (uint)(counter >> 32);
    uint s14 = blockLength;
    uint s15 = flags;

    // 第 1 轮
    s0 = s0 + s4 + message[0];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[1];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[2];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[3];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[4];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[5];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[6];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[7];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[8];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[9];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[10];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[11];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[12];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[13];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[14];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[15];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 2 轮
    s0 = s0 + s4 + message[2];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[6];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[3];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[10];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[7];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[0];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[4];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[13];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[1];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[11];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[12];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[5];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[9];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[14];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[15];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[8];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 3 轮
    s0 = s0 + s4 + message[3];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[4];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[10];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[12];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[13];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[2];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[7];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[14];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[6];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[5];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[9];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[0];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[11];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[15];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[8];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[1];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 4 轮
    s0 = s0 + s4 + message[10];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[7];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[12];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[9];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[14];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[3];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[13];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[15];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[4];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[0];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[11];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[2];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[5];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[8];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[1];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[6];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 5 轮
    s0 = s0 + s4 + message[12];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[13];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[9];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[11];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[15];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[10];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[14];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[8];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[7];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[2];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[5];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[3];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[0];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[1];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[6];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[4];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 6 轮
    s0 = s0 + s4 + message[9];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[14];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[11];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[5];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[8];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[12];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[15];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[1];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[13];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[3];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[0];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[10];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[2];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[6];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[4];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[7];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    // 第 7 轮
    s0 = s0 + s4 + message[11];
    s12 = (s12 ^ s0); s12 = (s12 >> 16) | (s12 << 16);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 12) | (s4 << 20);
    s0 = s0 + s4 + message[15];
    s12 = (s12 ^ s0); s12 = (s12 >> 8) | (s12 << 24);
    s8 = s8 + s12;
    s4 = (s4 ^ s8); s4 = (s4 >> 7) | (s4 << 25);

    s1 = s1 + s5 + message[5];
    s13 = (s13 ^ s1); s13 = (s13 >> 16) | (s13 << 16);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 12) | (s5 << 20);
    s1 = s1 + s5 + message[0];
    s13 = (s13 ^ s1); s13 = (s13 >> 8) | (s13 << 24);
    s9 = s9 + s13;
    s5 = (s5 ^ s9); s5 = (s5 >> 7) | (s5 << 25);

    s2 = s2 + s6 + message[1];
    s14 = (s14 ^ s2); s14 = (s14 >> 16) | (s14 << 16);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 12) | (s6 << 20);
    s2 = s2 + s6 + message[9];
    s14 = (s14 ^ s2); s14 = (s14 >> 8) | (s14 << 24);
    s10 = s10 + s14;
    s6 = (s6 ^ s10); s6 = (s6 >> 7) | (s6 << 25);

    s3 = s3 + s7 + message[8];
    s15 = (s15 ^ s3); s15 = (s15 >> 16) | (s15 << 16);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 12) | (s7 << 20);
    s3 = s3 + s7 + message[6];
    s15 = (s15 ^ s3); s15 = (s15 >> 8) | (s15 << 24);
    s11 = s11 + s15;
    s7 = (s7 ^ s11); s7 = (s7 >> 7) | (s7 << 25);

    s0 = s0 + s5 + message[14];
    s15 = (s15 ^ s0); s15 = (s15 >> 16) | (s15 << 16);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 12) | (s5 << 20);
    s0 = s0 + s5 + message[10];
    s15 = (s15 ^ s0); s15 = (s15 >> 8) | (s15 << 24);
    s10 = s10 + s15;
    s5 = (s5 ^ s10); s5 = (s5 >> 7) | (s5 << 25);

    s1 = s1 + s6 + message[2];
    s12 = (s12 ^ s1); s12 = (s12 >> 16) | (s12 << 16);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 12) | (s6 << 20);
    s1 = s1 + s6 + message[12];
    s12 = (s12 ^ s1); s12 = (s12 >> 8) | (s12 << 24);
    s11 = s11 + s12;
    s6 = (s6 ^ s11); s6 = (s6 >> 7) | (s6 << 25);

    s2 = s2 + s7 + message[3];
    s13 = (s13 ^ s2); s13 = (s13 >> 16) | (s13 << 16);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 12) | (s7 << 20);
    s2 = s2 + s7 + message[4];
    s13 = (s13 ^ s2); s13 = (s13 >> 8) | (s13 << 24);
    s8 = s8 + s13;
    s7 = (s7 ^ s8); s7 = (s7 >> 7) | (s7 << 25);

    s3 = s3 + s4 + message[7];
    s14 = (s14 ^ s3); s14 = (s14 >> 16) | (s14 << 16);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 12) | (s4 << 20);
    s3 = s3 + s4 + message[13];
    s14 = (s14 ^ s3); s14 = (s14 >> 8) | (s14 << 24);
    s9 = s9 + s14;
    s4 = (s4 ^ s9); s4 = (s4 >> 7) | (s4 << 25);

    output[outputOffset + 0] = s0 ^ s8;
    output[outputOffset + 1] = s1 ^ s9;
    output[outputOffset + 2] = s2 ^ s10;
    output[outputOffset + 3] = s3 ^ s11;
    output[outputOffset + 4] = s4 ^ s12;
    output[outputOffset + 5] = s5 ^ s13;
    output[outputOffset + 6] = s6 ^ s14;
    output[outputOffset + 7] = s7 ^ s15;
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
    return TryBuildTargetPath(template, rootDirectory, effectiveDate, missingDateText,
        baseName, extension, checksumBadge, null, out targetPath, out error);
}

// 批量预览时传入可复用的 values 缓冲区，避免每个文件重新分配模板词典。
public static bool TryBuildTargetPath(string template, string rootDirectory, DateTime? effectiveDate,
    string missingDateText, string baseName, string extension, string checksumBadge,
    IDictionary<string, string> values, out string targetPath, out string error)
{
    return TryBuildTargetPath(template, rootDirectory, effectiveDate, missingDateText,
        baseName, extension, checksumBadge, values, false, false, out targetPath, out error);
}

// 带路径合并选项的重载：合并只作用于模板生成的相对部分，不修改根路径本身。
public static bool TryBuildTargetPath(string template, string rootDirectory, DateTime? effectiveDate,
    string missingDateText, string baseName, string extension, string checksumBadge,
    IDictionary<string, string> values, bool mergeAdjacentSegments, bool mergeRepeatedSequences,
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

    if (values == null)
        values = new Dictionary<string, string>(StringComparer.Ordinal);
    values["【日期yyyy】"] = dateYear;
    values["【日期mm】"] = dateMonth;
    values["【日期dd】"] = dateDay;
    values["【根路径】"] = rootDirectory == null ? "" : rootDirectory;
    values["【源文件名】"] = baseName == null ? "" : baseName;
    values["【扩展名】"] = extension == null ? "" : extension;
    values["【校验码】"] = checksumBadge == null ? "" : checksumBadge;

    string rendered = RenderTemplate(template, values);
    string normalized = NormalizeTargetPath(rendered, rootDirectory,
        mergeAdjacentSegments, mergeRepeatedSequences, out error);
    if (normalized == null)
        return false;

    if (!ValidateCrossPlatformPath(normalized, out error))
        return false;

    targetPath = normalized;
    return true;
}

public static string NormalizeTargetPath(string renderedPath, string rootDirectory, out string error)
{
    return NormalizeTargetPath(renderedPath, rootDirectory, false, false, out error);
}

public static string NormalizeTargetPath(string renderedPath, string rootDirectory,
    bool mergeAdjacentSegments, bool mergeRepeatedSequences, out string error)
{
    error = null;
    if (renderedPath == null)
    {
        error = "目标路径为空";
        return null;
    }

    string path = renderedPath.Replace('/', '\\');
    path = CollapseBackslashes(path);
    bool merge = mergeAdjacentSegments || mergeRepeatedSequences;

    bool rooted = IsRootedWindowsPath(path);
    if (!rooted)
    {
        if (rootDirectory == null || rootDirectory.Trim().Length == 0)
        {
            error = "模板未包含【根路径】且根路径为空";
            return null;
        }
        string relative = path.TrimStart('\\');
        if (merge)
            relative = MergeTargetPathSegments(relative, mergeAdjacentSegments, mergeRepeatedSequences);
        string root = CollapseBackslashes(rootDirectory.Replace('/', '\\')).TrimEnd('\\');
        path = root + "\\" + relative;
        path = CollapseBackslashes(path);
    }
    else if (merge)
    {
        path = MergeTargetPathSegments(path, mergeAdjacentSegments, mergeRepeatedSequences);
    }

    return path;
}

// 合并路径中连续重复的文件夹段；最后一段视为文件名，不参与合并。
public static string MergeTargetPathSegments(string path, bool mergeAdjacentSegments,
    bool mergeRepeatedSequences)
{
    if (path == null || path.Length == 0 ||
        (!mergeAdjacentSegments && !mergeRepeatedSequences))
        return path;

    string prefix;
    List<string> parts;
    if (!TrySplitPathForMerge(path, out prefix, out parts) || parts.Count < 2)
        return path;

    List<string> dirs = new List<string>();
    for (int i = 0; i < parts.Count - 1; i++)
        dirs.Add(parts[i]);

    bool changed;
    do
    {
        changed = false;
        if (mergeRepeatedSequences)
        {
            while (CollapseOneRepeatedSequence(dirs))
                changed = true;
        }
        if (mergeAdjacentSegments)
        {
            while (CollapseOneAdjacentDuplicate(dirs))
                changed = true;
        }
    }
    while (changed);

    StringBuilder builder = new StringBuilder();
    builder.Append(prefix);
    for (int i = 0; i < dirs.Count; i++)
    {
        if (i > 0)
            builder.Append('\\');
        builder.Append(dirs[i]);
    }
    if (dirs.Count > 0)
        builder.Append('\\');
    builder.Append(parts[parts.Count - 1]);
    return builder.ToString();
}

// 拆分盘符/UNC 前缀与剩余路径段；无法识别标准根时返回 false，保持原路径不变。
private static bool TrySplitPathForMerge(string path, out string prefix, out List<string> parts)
{
    prefix = "";
    parts = new List<string>();
    if (path == null || path.Length == 0)
        return false;

    string[] raw = path.Split('\\');
    int start = 0;
    if (IsRootedWindowsPath(path))
    {
        if (path.Length >= 3 && IsAsciiDriveLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            prefix = raw[0] + "\\";
            start = 1;
        }
        else if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            if (raw.Length < 4 || raw[2].Length == 0 || raw[3].Length == 0)
                return false;
            prefix = "\\\\" + raw[2] + "\\" + raw[3] + "\\";
            start = 4;
        }
        else
        {
            return false;
        }
    }

    for (int i = start; i < raw.Length; i++)
        parts.Add(raw[i]);
    return parts.Count > 0;
}

// 合并最长的连续重复多级块（至少2段），每次删除后一块，直到没有重复。
private static bool CollapseOneRepeatedSequence(List<string> dirs)
{
    int maxLength = dirs.Count / 2;
    for (int length = maxLength; length >= 2; length--)
    {
        for (int start = 0; start + length * 2 <= dirs.Count; start++)
        {
            if (!PathBlocksEqual(dirs, start, start + length, length))
                continue;
            dirs.RemoveRange(start + length, length);
            return true;
        }
    }
    return false;
}

private static bool CollapseOneAdjacentDuplicate(List<string> dirs)
{
    for (int i = 0; i + 1 < dirs.Count; i++)
    {
        if (!string.Equals(dirs[i], dirs[i + 1], StringComparison.OrdinalIgnoreCase))
            continue;
        dirs.RemoveAt(i + 1);
        return true;
    }
    return false;
}

private static bool PathBlocksEqual(List<string> dirs, int left, int right, int length)
{
    for (int i = 0; i < length; i++)
    {
        if (!string.Equals(dirs[left + i], dirs[right + i], StringComparison.OrdinalIgnoreCase))
            return false;
    }
    return true;
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
    config["filterExtensions"] = "";
    config["filterMode"] = "skip";
    config["dateSource"] = "filename";
    config["dateFallbackOrder"] = "filename,exif,modified,created,empty";
    config["dateFallbackCustomText"] = "";
    config["preferExifOnFilenameConflict"] = "false";
    config["mergeAdjacentPathSegments"] = "false";
    config["mergeRepeatedPathSequences"] = "false";
    config["realTimePreview"] = "true";
    config["execParallel"] = "2";
    config["execVerify"] = "true";
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

    config["filterExtensions"] = NormalizeExtensionList(GetConfigString(config, "filterExtensions", ""));
    config["filterMode"] = NormalizeFilterMode(GetConfigString(config, "filterMode", "skip"));

    config["dateSource"] = NormalizeDateSource(GetConfigString(config, "dateSource", "filename"));
    string oldFallback5 = GetConfigString(config, "dateFallback5Text", "");
    string rawOrder = GetConfigString(config, "dateFallbackOrder", "");
    if (rawOrder == null || rawOrder.Trim().Length == 0)
    {
        rawOrder = "filename,exif,modified,created";
        rawOrder += "," + (oldFallback5.Length > 0 ? "custom" : "empty");
    }
    else if (rawOrder.Split(',').Length == 4 && oldFallback5.Length > 0)
    {
        rawOrder += ",custom";
    }
    config["dateFallbackOrder"] = NormalizeFallbackOrder(rawOrder);
    string customText = GetConfigString(config, "dateFallbackCustomText", oldFallback5);
    config["dateFallbackCustomText"] = customText == null ? "" : customText;
    config["preferExifOnFilenameConflict"] =
        GetConfigBool(config, "preferExifOnFilenameConflict", false) ? "true" : "false";
    config["mergeAdjacentPathSegments"] =
        GetConfigBool(config, "mergeAdjacentPathSegments", false) ? "true" : "false";
    config["mergeRepeatedPathSequences"] =
        GetConfigBool(config, "mergeRepeatedPathSequences", false) ? "true" : "false";
    config["realTimePreview"] = GetConfigBool(config, "realTimePreview", true) ? "true" : "false";
    config["execParallel"] = NormalizeParallelCount(GetConfigInt(config, "execParallel", 2)).ToString(CultureInfo.InvariantCulture);
    config["execVerify"] = GetConfigBool(config, "execVerify", true) ? "true" : "false";

    string algorithm = NormalizeAlgorithm(GetConfigString(config, "checksumAlgorithm", "CRC32"));
    if (!IsSupportedAlgorithm(algorithm))
        algorithm = "CRC32";
    config["checksumAlgorithm"] = algorithm;

    int digits;
    if (!int.TryParse(GetConfigString(config, "checksumDigits", "8"), NumberStyles.Integer, CultureInfo.InvariantCulture, out digits))
        digits = GetAlgorithmMaxDigits(algorithm);
    config["checksumDigits"] = ClampChecksumDigits(algorithm, digits).ToString(CultureInfo.InvariantCulture);

    config["checksumPrefix"] = GetConfigString(config, "checksumPrefix", "[");
    config["checksumSuffix"] = GetConfigString(config, "checksumSuffix", "]");

    string template = GetConfigString(config, "template", "");
    if (template == null || template.Trim().Length == 0)
        template = GetDefaultTemplateForMode(config["filterMode"]);
    config["template"] = template;
}

public static Dictionary<string, object> BuildPresetDictionary(Dictionary<string, string> config)
{
    Dictionary<string, string> normalized = config == null ? CreateDefaultConfig(2000) : config;
    NormalizeConfig(normalized);
    Dictionary<string, object> preset = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    preset["presetVersion"] = "0.05";
    for (int i = 0; i < PresetConfigKeys.Length; i++)
        preset[PresetConfigKeys[i]] = normalized[PresetConfigKeys[i]];
    return preset;
}

public static bool ApplyPresetDictionary(IDictionary<string, object> preset, Dictionary<string, string> config)
{
    if (preset == null || config == null)
        return false;

    bool applied = false;
    bool templateApplied = false;
    // 以公共配置键为基准，再补入旧版本兼容键，避免两处键表漂移。
    List<string> keys = new List<string>(PresetConfigKeys);
    keys.Add("dateFallback5Text");
    keys.Add("dateFallbackEnabled");
    keys.Add("dateMissingMode");
    keys.Add("dateMissingText");
    for (int i = 0; i < keys.Count; i++)
    {
        object raw;
        if (!preset.TryGetValue(keys[i], out raw) || raw == null)
            continue;
        config[keys[i]] = Convert.ToString(raw, CultureInfo.InvariantCulture);
        applied = true;
        if (keys[i] == "template")
            templateApplied = true;
    }
    if (applied)
    {
        if (!templateApplied)
        {
            string current = GetConfigString(config, "template", "");
            if (current == null || current.Length == 0 ||
                current == DefaultTemplate || current == RenameDefaultTemplate)
                config["template"] = GetDefaultTemplateForMode(GetConfigString(config, "filterMode", "skip"));
        }
        NormalizeConfig(config);
    }
    return applied;
}

// 预设词典统一为“预设名 -> 配置JSON字符串”的扁平结构。
// 这样Quicker“保存为状态”读回时仍是字符串值，不会因嵌套词典退化而丢失预设。
public static Dictionary<string, object> BuildFlatPresetDictionary(
    IDictionary<string, Dictionary<string, object>> store)
{
    Dictionary<string, object> flat =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    if (store == null)
        return flat;

    foreach (KeyValuePair<string, Dictionary<string, object>> pair in store)
    {
        if (pair.Key == null || pair.Key.Trim().Length == 0)
            continue;
        flat[pair.Key] = SerializeJsonValue(pair.Value);
    }
    return flat;
}

public static Dictionary<string, Dictionary<string, object>> ParsePresetDictionary(object value)
{
    Dictionary<string, Dictionary<string, object>> store =
        new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
    IDictionary<string, object> root = ConvertToObjectDictionary(value);
    if (root == null)
        return store;

    object presetsObject;
    if (root.TryGetValue("presets", out presetsObject) && presetsObject != null)
    {
        // 旧结构：{ presetVersion, presets: { 名称: 配置词典 } }。
        // 部分状态读回可能丢失 presetVersion，因此只要 presets 的值不是单套配置就按旧结构处理。
        IDictionary<string, object> presets = ConvertToObjectDictionary(presetsObject);
        if (presets != null && !LooksLikePresetConfigDictionary(presets))
        {
            foreach (KeyValuePair<string, object> pair in presets)
            {
                IDictionary<string, object> config = ConvertToObjectDictionary(pair.Value);
                if (config != null)
                    store[pair.Key] = CopyPresetDictionary(config);
            }
            return store;
        }
    }

    if (LooksLikePresetConfigDictionary(root))
    {
        // 更旧结构：单个配置词典直接作为预设。
        store["默认"] = CopyPresetDictionary(root);
        return store;
    }

    // 新结构：{ 预设名: 配置JSON字符串 }
    foreach (KeyValuePair<string, object> pair in root)
    {
        if (pair.Key == null || pair.Key.Trim().Length == 0)
            continue;
        IDictionary<string, object> config = ConvertToObjectDictionary(pair.Value);
        if (config != null)
            store[pair.Key] = CopyPresetDictionary(config);
    }
    return store;
}

// 扩展名预设与主预设使用同一种扁平结构：{ 预设名: 扩展名列表字符串 }。
// Quicker“保存为状态”读回时字符串值不会退化为嵌套词典。
public static Dictionary<string, object> BuildFlatExtensionPresetDictionary(
    IDictionary<string, string> store)
{
    Dictionary<string, object> flat =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    if (store == null)
        return flat;

    foreach (KeyValuePair<string, string> pair in store)
    {
        if (pair.Key == null || pair.Key.Trim().Length == 0)
            continue;
        flat[pair.Key] = pair.Value == null ? "" : pair.Value;
    }
    return flat;
}

public static Dictionary<string, string> ParseExtensionPresetDictionary(object value)
{
    Dictionary<string, string> store =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    IDictionary<string, object> root = ConvertToObjectDictionary(value);
    if (root == null)
        return store;

    object presetsObject;
    if (root.TryGetValue("presets", out presetsObject) && presetsObject != null)
    {
        // 兼容旧结构：{ presetVersion, presets: { 名称: 扩展名列表 } }。
        IDictionary<string, object> presets = ConvertToObjectDictionary(presetsObject);
        if (presets != null)
        {
            foreach (KeyValuePair<string, object> pair in presets)
            {
                if (pair.Key == null || pair.Key.Trim().Length == 0 || pair.Value == null)
                    continue;
                store[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
            }
            return store;
        }
    }

    foreach (KeyValuePair<string, object> pair in root)
    {
        if (pair.Key == null || pair.Key.Trim().Length == 0 || pair.Value == null)
            continue;
        if (pair.Key == "presetVersion" && !(pair.Value is IDictionary))
            continue;
        if (pair.Value is IDictionary)
            continue;
        store[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
    }
    return store;
}

public static string SerializeJsonValue(object value)
{
    StringBuilder builder = new StringBuilder();
    AppendJsonValue(builder, value);
    return builder.ToString();
}

private static void AppendJsonValue(StringBuilder builder, object value)
{
    if (value == null)
    {
        builder.Append("null");
        return;
    }

    string text = value as string;
    if (text != null)
    {
        AppendJsonString(builder, text);
        return;
    }

    if (value is bool)
    {
        builder.Append(((bool)value) ? "true" : "false");
        return;
    }

    if (value is int || value is long || value is short || value is byte ||
        value is uint || value is ulong || value is ushort || value is sbyte ||
        value is double || value is float || value is decimal)
    {
        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        return;
    }

    IDictionary<string, object> generic = value as IDictionary<string, object>;
    if (generic != null)
    {
        AppendJsonObject(builder, generic);
        return;
    }

    IDictionary dictionary = value as IDictionary;
    if (dictionary != null)
    {
        Dictionary<string, object> converted =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
            converted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
        AppendJsonObject(builder, converted);
        return;
    }

    IEnumerable enumerable = value as IEnumerable;
    if (enumerable != null)
    {
        builder.Append('[');
        bool first = true;
        foreach (object item in enumerable)
        {
            if (!first)
                builder.Append(',');
            first = false;
            AppendJsonValue(builder, item);
        }
        builder.Append(']');
        return;
    }

    AppendJsonString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
}

private static void AppendJsonObject(StringBuilder builder, IDictionary<string, object> dictionary)
{
    builder.Append('{');
    bool first = true;
    foreach (KeyValuePair<string, object> pair in dictionary)
    {
        if (!first)
            builder.Append(',');
        first = false;
        AppendJsonString(builder, pair.Key == null ? "" : pair.Key);
        builder.Append(':');
        AppendJsonValue(builder, pair.Value);
    }
    builder.Append('}');
}

private static void AppendJsonString(StringBuilder builder, string text)
{
    builder.Append('"');
    if (text != null)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '"' || ch == '\\')
            {
                builder.Append('\\');
                builder.Append(ch);
            }
            else if (ch == '\b') builder.Append("\\b");
            else if (ch == '\f') builder.Append("\\f");
            else if (ch == '\n') builder.Append("\\n");
            else if (ch == '\r') builder.Append("\\r");
            else if (ch == '\t') builder.Append("\\t");
            else if (ch < ' ' || ch == '\u2028' || ch == '\u2029')
            {
                builder.Append("\\u");
                builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(ch);
            }
        }
    }
    builder.Append('"');
}

public static IDictionary<string, object> ConvertToObjectDictionary(object value)
{
    if (value == null)
        return null;

    IDictionary<string, object> generic = value as IDictionary<string, object>;
    if (generic != null)
        return generic;

    IDictionary dictionary = value as IDictionary;
    if (dictionary != null)
    {
        Dictionary<string, object> converted =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
            converted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
        return converted;
    }

    string text = value as string;
    if (text == null)
        return null;

    object parsed;
    if (!TryParseJson(text, out parsed))
        return null;
    return ConvertToObjectDictionary(parsed);
}

private static Dictionary<string, object> CopyPresetDictionary(IDictionary<string, object> source)
{
    Dictionary<string, object> copy =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    foreach (KeyValuePair<string, object> pair in source)
        copy[pair.Key] = pair.Value;
    return copy;
}

private static bool LooksLikePresetConfigDictionary(IDictionary<string, object> dictionary)
{
    if (dictionary == null)
        return false;
    // 单套配置同时包含多个配置键；要求至少命中2个，避免把名为 template、
    // rootMode 等配置键的扁平预设误判为旧版单预设。
    int matches = 0;
    for (int i = 0; i < PresetConfigKeys.Length; i++)
    {
        if (dictionary.ContainsKey(PresetConfigKeys[i]))
            matches++;
        if (matches >= 2)
            return true;
    }
    return false;
}

// Quicker“保存为状态”会把嵌套词典序列化成JSON文本；启动时用以下解析器还原。
public static bool TryParseJson(string text, out object value)
{
    value = null;
    if (text == null)
        return false;

    string trimmed = text.Trim();
    if (trimmed.Length == 0)
        return false;

    int index = 0;
    try
    {
        value = ParseJsonValue(trimmed, ref index, 0);
        SkipJsonWhitespace(trimmed, ref index);
        return index == trimmed.Length;
    }
    catch
    {
        value = null;
        return false;
    }
}

// 限制嵌套深度，避免恶意或损坏的状态JSON造成栈溢出。
private const int MaxJsonDepth = 64;

private static object ParseJsonValue(string text, ref int index, int depth)
{
    if (depth > MaxJsonDepth)
        throw new FormatException("JSON嵌套层级过深。");
    SkipJsonWhitespace(text, ref index);
    if (index >= text.Length)
        throw new FormatException("JSON意外结束。");

    char ch = text[index];
    if (ch == '{')
        return ParseJsonObject(text, ref index, depth + 1);
    if (ch == '[')
        return ParseJsonArray(text, ref index, depth + 1);
    if (ch == '"')
        return ParseJsonString(text, ref index);
    if (ch == 't')
    {
        ExpectJsonLiteral(text, ref index, "true");
        return true;
    }
    if (ch == 'f')
    {
        ExpectJsonLiteral(text, ref index, "false");
        return false;
    }
    if (ch == 'n')
    {
        ExpectJsonLiteral(text, ref index, "null");
        return null;
    }
    return ParseJsonNumber(text, ref index);
}

private static Dictionary<string, object> ParseJsonObject(string text, ref int index, int depth)
{
    Dictionary<string, object> result =
        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    index++;

    SkipJsonWhitespace(text, ref index);
    if (index < text.Length && text[index] == '}')
    {
        index++;
        return result;
    }

    while (true)
    {
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '"')
            throw new FormatException("JSON对象键必须是字符串。");

        string key = ParseJsonString(text, ref index);
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length || text[index] != ':')
            throw new FormatException("JSON对象缺少冒号。");

        index++;
        result[key] = ParseJsonValue(text, ref index, depth);
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length)
            throw new FormatException("JSON对象未结束。");

        if (text[index] == ',')
        {
            index++;
            continue;
        }
        if (text[index] == '}')
        {
            index++;
            return result;
        }
        throw new FormatException("JSON对象分隔符无效。");
    }
}

private static List<object> ParseJsonArray(string text, ref int index, int depth)
{
    List<object> result = new List<object>();
    index++;

    SkipJsonWhitespace(text, ref index);
    if (index < text.Length && text[index] == ']')
    {
        index++;
        return result;
    }

    while (true)
    {
        result.Add(ParseJsonValue(text, ref index, depth));
        SkipJsonWhitespace(text, ref index);
        if (index >= text.Length)
            throw new FormatException("JSON数组未结束。");

        if (text[index] == ',')
        {
            index++;
            continue;
        }
        if (text[index] == ']')
        {
            index++;
            return result;
        }
        throw new FormatException("JSON数组分隔符无效。");
    }
}

private static string ParseJsonString(string text, ref int index)
{
    StringBuilder builder = new StringBuilder();
    index++;

    while (index < text.Length)
    {
        char ch = text[index++];
        if (ch == '"')
            return builder.ToString();

        if (ch != '\\')
        {
            builder.Append(ch);
            continue;
        }

        if (index >= text.Length)
            throw new FormatException("JSON字符串转义不完整。");

        char escape = text[index++];
        switch (escape)
        {
            case '"': builder.Append('"'); break;
            case '\\': builder.Append('\\'); break;
            case '/': builder.Append('/'); break;
            case 'b': builder.Append('\b'); break;
            case 'f': builder.Append('\f'); break;
            case 'n': builder.Append('\n'); break;
            case 'r': builder.Append('\r'); break;
            case 't': builder.Append('\t'); break;
            case 'u':
                if (index + 4 > text.Length)
                    throw new FormatException("JSON Unicode转义不完整。");
                int code;
                if (!int.TryParse(text.Substring(index, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out code))
                    throw new FormatException("JSON Unicode转义无效。");
                builder.Append((char)code);
                index += 4;
                break;
            default:
                throw new FormatException("JSON字符串转义无效。");
        }
    }
    throw new FormatException("JSON字符串未结束。");
}

private static object ParseJsonNumber(string text, ref int index)
{
    int start = index;
    if (index < text.Length && (text[index] == '-' || text[index] == '+'))
        index++;

    while (index < text.Length)
    {
        char ch = text[index];
        if (char.IsDigit(ch) || ch == '.' || ch == 'e' || ch == 'E' || ch == '+' || ch == '-')
        {
            index++;
            continue;
        }
        break;
    }

    string token = text.Substring(start, index - start);
    double number;
    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        throw new FormatException("JSON数字无效。");
    return number;
}

private static void ExpectJsonLiteral(string text, ref int index, string literal)
{
    if (index + literal.Length > text.Length ||
        string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
        throw new FormatException("JSON字面量无效。");
    index += literal.Length;
}

private static void SkipJsonWhitespace(string text, ref int index)
{
    while (index < text.Length && char.IsWhiteSpace(text[index]))
        index++;
}
