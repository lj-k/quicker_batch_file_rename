using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
// 批量重命名器界面逻辑（Quicker 自定义窗口辅助 C# 代码）
// 版本：0.06.6
// 部署时与 RenamerCore.cs 合并，放入【辅助C#代码】。
// 兼容 C# 5.0；三个 Quicker 回调：OnWindowCreated / OnWindowLoaded /
// OnButtonClicked。按钮事件在 OnWindowLoaded 中通过 Click 注册。
// ============================================================================

// 交付模式：0=完整开发版；1=仅重命名不执行；2=执行不输出列表。
// 发布脚本会把该常量替换为对应方案的值。
private static readonly int DeliveryMode = 0;

private static Window _window;
private static IDictionary<string, object> _data;
private static ICustomWindowContext _winContext;
private static DataGrid _grid;
private static RichTextBox _editor;
private static TextBox _rootPathText;
private static TextBox _digitsText;
private static TextBox _prefixText;
private static TextBox _suffixText;
private static ComboBox _rootModeCombo;
private static ComboBox _startYearCombo;
private static ComboBox _dateSourceCombo;
private static CheckBox _preferExifCheck;
private static ComboBox _algorithmCombo;
private static TextBlock _checksumBadge;
private static TextBlock _statusText;
private static ProgressBar _progress;
private static Button _browseRootButton;
private static Button _applyButton;
private static Button _finishButton;
private static Button _previewButton;
private static CheckBox _realTimeCheck;
private static TextBox _filterExtensionsText;
private static ComboBox _filterModeCombo;
private static Button _loadExtPresetButton;
private static Button _saveExtPresetButton;
private static ComboBox _fallback1Combo;
private static ComboBox _fallback2Combo;
private static ComboBox _fallback3Combo;
private static ComboBox _fallback4Combo;
private static ComboBox _fallback5Combo;
private static TextBox _fallbackCustomText;
private static Border _errorBanner;
private static TextBlock _errorBannerText;
private static Popup _presetPopup;
private static ListBox _presetListBox;
private static Button _loadPresetButton;
private static Button _savePresetButton;
private static ScrollViewer _configPanel;
private static ComboBox _execParallelCombo;
private static CheckBox _execVerifyCheck;
private static Button _executeButton;
private static ProgressBar _execProgress;
private static TextBlock _execStatusText;

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
private static bool _previewDirty = false;
private static bool _suppressPresetSelection = false;
private static volatile bool _hashStop = false;
private static volatile bool _hashRunning = false;
private static string _hashAlgorithm = "";
private static volatile int _scanGeneration = 0;
private static volatile int _hashGeneration = 0;
private static volatile int _hashTotal = 0;
private static volatile int _hashDone = 0;
private static int _skippedFolders = 0;
private static int _skippedInvalid = 0;

private static bool _executing = false;
private static volatile bool _execStop = false;
private static bool _closeAfterExecution = false;
private static int _execCursor = 0;
private static int _execTotal = 0;
private static int _execDone = 0;
private static int _execErrors = 0;
private static int _execWorkers = 0;
private static long _execBytes = 0;
private static long _execStartTicks = 0;
private static List<Dictionary<string, object>> _execQueue = new List<Dictionary<string, object>>();
private static DispatcherTimer _execTimer = null;
private static readonly object _execRowLock = new object();

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
    _winContext = winContext;

    if (dataContext != null)
    {
        if (!dataContext.ContainsKey("files") || dataContext["files"] == null)
            dataContext["files"] = new List<object>();
        // preset/extensionPresets 由数据映射传入；缺失时在 OnWindowLoaded 尝试读取动作变量，
        // 不在这里提前写入空词典，避免掩盖“未配置映射”的问题。
        object startYearValue;
        if (!dataContext.TryGetValue("startYear", out startYearValue) || startYearValue == null)
            dataContext["startYear"] = 2000;

        // 取消或直接关闭窗口时，输出为空列表，避免残留上一次的结果。
        // 交付方案2不生成/输出列表变量。
        if (DeliveryMode != 2)
        {
            dataContext["origPaths"] = new List<object>();
            dataContext["targetPaths"] = new List<object>();
            dataContext["actions"] = new List<object>();
        }
    }
}

public static void OnWindowLoaded(Window win, IDictionary<string, object> dataContext,
    ICustomWindowContext winContext)
{
    _window = win;
    _data = dataContext;
    _winContext = winContext;

    PrimePresetVariables();

    FindControls(win);
    ResetRuntimeState();
    PopulateStartYears();
    _config = CreateDefaultConfig(GetInitialStartYear());
    InitializeTemplateEditor();
    SyncUiFromConfig();
    AttachEvents(win);
    AttachToolTips();

    _grid.ItemsSource = _rows;

    CollectInputPaths();
    if (_paths.Count == 0)
    {
        _statusText.Text = "没有可处理的文件" + DescribeSkipped();
        SetApplyEnabled(false);
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
    _preferExifCheck = (CheckBox)win.FindName("PreferExifCheck");
    _algorithmCombo = (ComboBox)win.FindName("AlgorithmCombo");
    _checksumBadge = (TextBlock)win.FindName("ChecksumBadge");
    _statusText = (TextBlock)win.FindName("StatusText");
    _progress = (ProgressBar)win.FindName("Progress");
    _browseRootButton = (Button)win.FindName("BrowseRootButton");
    _applyButton = (Button)win.FindName("BtnApply");
    _finishButton = (Button)win.FindName("BtnFinish");
    _previewButton = (Button)win.FindName("BtnPreview");
    _realTimeCheck = (CheckBox)win.FindName("RealTimeCheck");
    _filterExtensionsText = (TextBox)win.FindName("FilterExtensionsText");
    _filterModeCombo = (ComboBox)win.FindName("FilterModeCombo");
    _loadExtPresetButton = (Button)win.FindName("BtnLoadExtPreset");
    _saveExtPresetButton = (Button)win.FindName("BtnSaveExtPreset");
    _fallback1Combo = (ComboBox)win.FindName("Fallback1Combo");
    _fallback2Combo = (ComboBox)win.FindName("Fallback2Combo");
    _fallback3Combo = (ComboBox)win.FindName("Fallback3Combo");
    _fallback4Combo = (ComboBox)win.FindName("Fallback4Combo");
    _fallback5Combo = (ComboBox)win.FindName("Fallback5Combo");
    _fallbackCustomText = (TextBox)win.FindName("FallbackCustomText");
    _errorBanner = (Border)win.FindName("ErrorBanner");
    _errorBannerText = (TextBlock)win.FindName("ErrorBannerText");
    _presetPopup = (Popup)win.FindName("PresetPopup");
    _presetListBox = (ListBox)win.FindName("PresetListBox");
    _loadPresetButton = (Button)win.FindName("BtnLoadPreset");
    _savePresetButton = (Button)win.FindName("BtnSavePreset");
    _configPanel = (ScrollViewer)win.FindName("ConfigPanel");
    _execParallelCombo = (ComboBox)win.FindName("ExecParallelCombo");
    _execVerifyCheck = (CheckBox)win.FindName("ExecVerifyCheck");
    _executeButton = (Button)win.FindName("BtnExecute");
    _execProgress = (ProgressBar)win.FindName("ExecProgress");
    _execStatusText = (TextBlock)win.FindName("ExecStatusText");
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
    _previewDirty = false;
    _suppressPresetSelection = false;
    _suppressEvents = false;
    _suppressEditor = false;
    _executing = false;
    _execStop = false;
    _closeAfterExecution = false;
    _execCursor = 0;
    _execTotal = 0;
    _execDone = 0;
    _execErrors = 0;
    _execWorkers = 0;
    _execBytes = 0;
    _execStartTicks = 0;
    _execQueue = new List<Dictionary<string, object>>();
    StopExecutionTimer();
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
    _preferExifCheck.Checked += PreferExifCheck_Changed;
    _preferExifCheck.Unchecked += PreferExifCheck_Changed;
    _algorithmCombo.SelectionChanged += AlgorithmCombo_SelectionChanged;
    _realTimeCheck.Checked += RealTimeCheck_Changed;
    _realTimeCheck.Unchecked += RealTimeCheck_Changed;
    _filterExtensionsText.TextChanged += FilterExtensions_TextChanged;
    _filterModeCombo.SelectionChanged += FilterMode_SelectionChanged;
    if (_execParallelCombo != null)
        _execParallelCombo.SelectionChanged += ExecSetting_Changed;
    if (_execVerifyCheck != null)
    {
        _execVerifyCheck.Checked += ExecSetting_Changed;
        _execVerifyCheck.Unchecked += ExecSetting_Changed;
    }
    _fallback1Combo.SelectionChanged += FallbackOrder_SelectionChanged;
    _fallback2Combo.SelectionChanged += FallbackOrder_SelectionChanged;
    _fallback3Combo.SelectionChanged += FallbackOrder_SelectionChanged;
    _fallback4Combo.SelectionChanged += FallbackOrder_SelectionChanged;
    _fallback5Combo.SelectionChanged += FallbackOrder_SelectionChanged;
    _fallbackCustomText.TextChanged += FallbackCustomText_TextChanged;
    if (_presetListBox != null)
        _presetListBox.SelectionChanged += PresetListBox_SelectionChanged;
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
    AttachClick("BtnLoadExtPreset", LoadExtPreset_Click);
    AttachClick("BtnSaveExtPreset", SaveExtPreset_Click);
    AttachClick("BtnPreview", Preview_Click);
    AttachClick("BtnCancel", Cancel_Click);
    AttachClick("BtnApply", Apply_Click);
    AttachClick("BtnFinish", Finish_Click);
    AttachClick("BtnExecute", Execute_Click);

    win.Closed += Window_Closed;
    win.Closing += Window_Closing;
}

private static void AttachClick(string name, RoutedEventHandler handler)
{
    Button button = _window.FindName(name) as Button;
    if (button != null)
        button.Click += handler;
}

private static void AttachToolTips()
{
    SetTip(_rootModeCombo,
        "根路径模式。\n当前文件夹：逐个使用每个源文件所在目录。\n其他文件夹：所有文件写入同一个固定目录；选择后会弹出文件夹选择器。\n后果：根路径决定目标路径的前缀，可能导致不同源目录的文件汇总到同一目录并产生重名。");
    SetTip(_rootPathText, "当前根路径的显示结果，只读。\n当前文件夹模式下显示占位说明；其他文件夹模式下显示已选择目录。");
    SetTip(_browseRootButton, "选择固定根目录。\n选择“其他文件夹”时也会自动弹出此选择器；取消且没有历史目录时会回到“当前文件夹”。");
    SetTip(_startYearCombo, "文件名日期允许的最早起始年份，默认2000。\n修改后会重新提取所有文件的【文件名中的日期】，并刷新目标路径预览。");
    SetTip(_dateSourceCombo, "主选日期来源，默认“文件名中的日期”。\n该日期用于【日期yyyy】【日期mm】【日期dd】。\n主选缺失时按递补1→5依次处理。");
    SetTip(_preferExifCheck, "文件名日期与EXIF拍摄日期都可用、但日历日期不同时，输出EXIF拍摄日期。\n用于避免文件名中的日期识别错误；只有文件名日期参与生效日期时才覆盖。\n默认关闭；开启后会立即刷新预览。");
    SetTip(_filterExtensionsText, "过滤器扩展名列表。\n支持分号、逗号、空格、换行分隔；可写 jpg 或 .jpg。\n空列表表示不过滤。\n决策模式为跳过时作为排除名单；为复制/移动/重命名时作为白名单。");
    SetTip(_filterModeCombo, "过滤器决策模式。\n跳过：命中扩展名的文件跳过，其余文件按模板执行（同目录为重命名，跨目录为复制）。\n复制/移动/重命名：仅命中扩展名的文件按该动作执行，其余文件跳过；空列表表示不过滤。\n重命名模式下若模板加入中间文件夹，动作会自动变为复制。");
    SetTip(_loadExtPresetButton, "从动作词典【扩展名预设】载入扩展名列表。\n选择预设后可覆盖当前列表或追加到当前列表。");
    SetTip(_saveExtPresetButton, "把当前扩展名列表保存到动作词典【扩展名预设】。\n会要求输入预设名称；同名时确认覆盖。");
    SetTip(_fallback1Combo, "递补1：日期来源/空值/自定义/跳过。\n按顺序查找；日期来源可用则采用。\n空值=日期胶囊为空；自定义=使用自定义值；跳过=该文件不处理也不输出。");
    SetTip(_fallback2Combo, "递补2：前一项未取到日期时处理。\n可选项：文件名日期、EXIF、修改、创建、空值、自定义、跳过。");
    SetTip(_fallback3Combo, "递补3：前面各项都未取到日期时处理。\n空值/自定义/跳过会立即结束查找。");
    SetTip(_fallback4Combo, "递补4：前面各项都未取到日期时处理。\n空值/自定义/跳过会立即结束查找。");
    SetTip(_fallback5Combo, "递补5：最终兜底。默认“空值”。\n空值下【日期yyyy】【日期mm】【日期dd】全部为空。\n选择“跳过”则命中此兜底的文件不处理、不输出。");
    SetTip(_fallbackCustomText, "任一递补选择“自定义”时使用的文本。\n该文本会同时替换三个日期胶囊；为空时视为空值。\n后果：为空且默认模板包含日期层级时可能形成非法路径并报错。");
    SetTipByName("FilterCardTitle", "过滤器：按扩展名筛选文件。\n决策模式支持“匹配时跳过”或“匹配时执行”。\n扩展名列表可保存/载入到动作词典【扩展名预设】；载入可选择覆盖或追加。\n被跳过的文件不参与目标计算、不输出。");
    SetTipByName("RootCardTitle", "根路径：决定目标路径的根目录。\n当前文件夹=每个源文件所在目录；其他文件夹=统一目录。\n后果：统一目录可能让不同来源的同名文件产生重名。");
    SetTipByName("DateCardTitle", "日期：选择主日期来源，并配置递补1→5。\n递补可依次尝试其他日期、空值、自定义字符串；选择跳过时该文件不处理、不输出。\n空值下三个日期胶囊都为空。");
    SetTipByName("ChecksumCardTitle", "校验码：算法、位数、前后缀。\n位数取高位；默认前缀[、后缀]；模板不含【校验码】时不计算哈希。\n大文件/BLAKE3计算需要时间。");
    SetTipByName("TemplateCardTitle", "目标路径模板：使用右侧胶囊组合新路径和文件名。\n默认包含日期层级和aaa/bbb；输出统一Windows反斜杠。\n非法字符、保留设备名、超长路径或重名会在顶部报错。");
    SetTip(_realTimeCheck, "实时刷新开关。\n开启：配置变更后立即重算目标路径（文本输入约250ms防抖）。\n关闭：配置变更只更新配置/日期数据，目标路径保持旧值；需点击【预览】刷新，预览过期时【生成输出】禁用。");
    SetTip(_previewButton, "按当前配置重新计算目标路径，并启动必要的校验码计算。\n用于实时刷新关闭后的手动刷新，也可主动重新预览。");
    SetTip(_loadPresetButton, "点击后在下方弹出预设下拉列表。\n选择某个预设会载入其全部配置并刷新预览。\n若词典是旧版单预设，会显示为“默认”。");
    SetTip(_savePresetButton, "保存当前全部配置为命名预设。\n会要求输入预设名称；若同名预设已存在，会列出变更项目并询问是否覆盖。\n预设保存在动作词典【批量重命名器预设】中。");
    SetTip(_algorithmCombo, "校验算法。\n最高16进制位数：CRC32=8、BLAKE3=64、MD5=32、SHA-256=64。\n切换算法时，若原位数是旧算法最高值，会自动改为新算法最高值。");
    SetTip(_digitsText, "保留的16进制位数，1~算法最高值。\n截取取高位；等于最高值时不显示“_位数”。\n位数越短，重名概率越高。");
    SetTip(_prefixText, "校验码前缀，默认“[”。\n直接位于“算法-校验码”左侧，不再额外套固定方括号。");
    SetTip(_suffixText, "校验码后缀，默认“]”。\n直接位于“算法-校验码”右侧。");
    SetTip(_editor, "目标路径模板。\n点击右侧胶囊可在光标位置插入变量。\n【根路径】可与相对子目录组合；也可直接写绝对路径。\n输出统一使用Windows反斜杠；非法字符、保留设备名、超长路径会在顶部报错。");
    SetTip(_grid, "源路径、目标路径与动作预览。\n鼠标悬浮行可查看四类日期、生效日期、校验码、目标路径和完整错误。\n红底=异常，黄底=校验码计算中，灰底=跳过，绿底=执行成功。");
    SetTip(_statusText, "状态统计：文件总数、跳过项、就绪/异常/计算中数量与校验码进度。\n实时刷新关闭且配置变更后会提示需要手动预览。");
    SetTipByName("BtnCancel", "关闭窗口。\n未执行时：放弃输出，两个输出列表保持为空。\n执行中：询问是否停止；已开始的文件操作会完成，未开始的项目不再执行。");
    SetTip(_applyButton, "方案1：按钮显示为【执行】，生成并输出三个非跳过列表（原路径、目标路径、动作），然后关闭窗口继续 Quicker 后续动作；不会修改文件。\n完整开发版：生成两个列表/预设并关闭窗口。\n存在异常行时需确认，仅输出就绪行；校验码未算完时需等待。");
    SetTip(_finishButton, "完成并关闭窗口，继续执行 Quicker 后续动作步骤。\n方案1会写回两个非跳过路径列表；方案2不生成/输出列表变量，只写回预设。");
    SetTip(_execParallelCombo, "执行并行线程数，1~8。\n数值越大复制/移动越快，但会占用更多磁盘与CPU资源。\n重命名操作本身很快，通常无需高并行。");
    SetTip(_execVerifyCheck, "执行后校验。\n复制/移动完成后重新计算目标文件校验码，与源文件校验码比较；\n若模板包含【校验码】，还会检查目标文件名中的校验码标记是否一致。\n重命名不执行该校验。大文件会明显增加耗时。");
    SetTip(_executeButton, "按展示列表的“动作”列执行复制/移动/重命名。\n跳过行、异常行和校验码计算中的行不会执行。\n目标目录不存在时自动创建；目标文件已存在时该项报错，不会覆盖。\n执行期间配置区禁用，可点击取消停止尚未开始的项目。");

    SetTip(_presetListBox, "预设下拉列表。\n单击预设名称即可载入其全部配置；点击列表外可关闭。");

    SetTipByName("BtnTokenRoot", "插入【根路径】：当前文件所在目录或固定根目录。");
    SetTipByName("BtnTokenYyyy", "插入【日期yyyy】：生效日期的四位年份。");
    SetTipByName("BtnTokenMm", "插入【日期mm】：两位月份。");
    SetTipByName("BtnTokenDd", "插入【日期dd】：两位日期。");
    SetTipByName("BtnTokenHash", "插入【校验码】：按当前算法、位数、前后缀生成的校验码。");
    SetTipByName("BtnTokenName", "插入【源文件名】：不含扩展名的源文件名。");
    SetTipByName("BtnTokenExt", "插入【扩展名】：不含点的源扩展名；无扩展名时可能形成结尾点。");
    SetTipByName("BtnResetTemplate", "恢复默认模板：【根路径】/【日期yyyy】.【日期mm】/aaa/bbb/【源文件名】-【校验码】.【扩展名】。");
}

private static void SetTipByName(string name, string text)
{
    FrameworkElement element = _window.FindName(name) as FrameworkElement;
    SetTip(element, text);
}

private static void SetTip(FrameworkElement element, string text)
{
    if (element == null)
        return;
    TextBlock content = new TextBlock();
    content.Text = text;
    content.TextWrapping = TextWrapping.Wrap;
    content.MaxWidth = 480;
    content.FontSize = 12;
    ToolTip tip = new ToolTip();
    tip.Content = content;
    element.ToolTip = tip;
    ToolTipService.SetShowDuration(element, 60000);
    ToolTipService.SetShowOnDisabled(element, true);
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
    _preferExifCheck.IsChecked = GetConfigBool(_config, "preferExifOnFilenameConflict", false);
    SelectComboByTag(_algorithmCombo, _config["checksumAlgorithm"]);
    _realTimeCheck.IsChecked = GetConfigBool(_config, "realTimePreview", true);
    _filterExtensionsText.Text = _config["filterExtensions"];
    SelectComboByTag(_filterModeCombo, _config["filterMode"]);
    SelectComboByTag(_execParallelCombo, _config["execParallel"]);
    if (_execVerifyCheck != null)
        _execVerifyCheck.IsChecked = GetConfigBool(_config, "execVerify", true);
    SyncFallbackCombosFromConfig();
    _fallbackCustomText.Text = _config["dateFallbackCustomText"];
    _digitsText.Text = _config["checksumDigits"];
    _prefixText.Text = _config["checksumPrefix"];
    _suffixText.Text = _config["checksumSuffix"];
    SetTemplateText(_config["template"]);
    _suppressEvents = false;

    UpdateRootPathText();
    UpdateChecksumBadge();
    UpdateFallbackUiState();
    UpdateRealTimeUi();
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
    if (combo == null)
        return;
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

private static string GetFallbackOrderFromUi()
{
    string[] tags = new string[] {
        GetComboTag(_fallback1Combo), GetComboTag(_fallback2Combo),
        GetComboTag(_fallback3Combo), GetComboTag(_fallback4Combo),
        GetComboTag(_fallback5Combo)
    };
    StringBuilder builder = new StringBuilder();
    for (int i = 0; i < tags.Length; i++)
    {
        if (tags[i] == null || tags[i].Length == 0)
            continue;
        if (builder.Length > 0)
            builder.Append(',');
        builder.Append(tags[i]);
    }
    return builder.ToString();
}

private static void SyncFallbackCombosFromConfig()
{
    string order = NormalizeFallbackOrder(GetConfigString(_config, "dateFallbackOrder", ""));
    string[] parts = order.Split(',');
    SelectComboByTag(_fallback1Combo, parts.Length > 0 ? parts[0] : "filename");
    SelectComboByTag(_fallback2Combo, parts.Length > 1 ? parts[1] : "exif");
    SelectComboByTag(_fallback3Combo, parts.Length > 2 ? parts[2] : "modified");
    SelectComboByTag(_fallback4Combo, parts.Length > 3 ? parts[3] : "created");
    SelectComboByTag(_fallback5Combo, parts.Length > 4 ? parts[4] : "empty");
}

private static void UpdateFallbackUiState()
{
    bool custom = GetComboTag(_fallback1Combo) == "custom" ||
        GetComboTag(_fallback2Combo) == "custom" ||
        GetComboTag(_fallback3Combo) == "custom" ||
        GetComboTag(_fallback4Combo) == "custom" ||
        GetComboTag(_fallback5Combo) == "custom";
    _fallbackCustomText.IsEnabled = custom;
}

private static void UpdateRealTimeUi()
{
    bool realTime = GetConfigBool(_config, "realTimePreview", true);
    _previewButton.IsEnabled = !realTime && !_scanning;
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

    _config["filterExtensions"] = _filterExtensionsText.Text == null ? "" : _filterExtensionsText.Text;
    _config["filterMode"] = GetComboTag(_filterModeCombo) ?? "skip";
    _config["dateSource"] = GetComboTag(_dateSourceCombo) ?? "filename";
    _config["dateFallbackOrder"] = GetFallbackOrderFromUi();
    _config["dateFallbackCustomText"] = _fallbackCustomText.Text == null ? "" : _fallbackCustomText.Text;
    _config["preferExifOnFilenameConflict"] = _preferExifCheck.IsChecked == true ? "true" : "false";
    _config["realTimePreview"] = _realTimeCheck.IsChecked == true ? "true" : "false";
    _config["execParallel"] = GetComboTag(_execParallelCombo) ?? "2";
    _config["execVerify"] = _execVerifyCheck != null && _execVerifyCheck.IsChecked == true ? "true" : "false";
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
    SetTip(_checksumBadge, "当前校验码格式预览。\n完整格式：" + badge +
        "\n位数小于算法最高值时显示“算法_位数”；截取取高位；前后缀直接包裹算法标识与校验码。");
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

private static void PreferExifCheck_Changed(object sender, RoutedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void AlgorithmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void RealTimeCheck_Changed(object sender, RoutedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void FilterExtensions_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEvents) return;
    ScheduleApply();
}

private static void FilterMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;

    // 重命名模式的默认模板不带日期层级；仅当模板仍是“默认模板”时才自动切换，
    // 避免覆盖用户已经手工修改的模板。
    string newMode = GetComboTag(_filterModeCombo) ?? "skip";
    string currentTemplate = GetTemplateText();
    string currentTrimmed = currentTemplate == null ? "" : currentTemplate.Trim();
    string oldDefault = GetDefaultTemplateForMode(_config["filterMode"]);
    if (currentTrimmed.Length == 0 ||
        string.Equals(currentTrimmed, oldDefault, StringComparison.Ordinal))
    {
        string newDefault = GetDefaultTemplateForMode(newMode);
        if (!string.Equals(oldDefault, newDefault, StringComparison.Ordinal))
        {
            _suppressEditor = true;
            SetTemplateText(newDefault);
            _suppressEditor = false;
        }
    }
    ApplyUiConfig();
}

private static void ExecSetting_Changed(object sender, RoutedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void FallbackOrder_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressEvents) return;
    ApplyUiConfig();
}

private static void FallbackCustomText_TextChanged(object sender, TextChangedEventArgs e)
{
    if (_suppressEvents) return;
    ScheduleApply();
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

    if (_debounceTimer != null)
        _debounceTimer.Stop();

    string oldStartYear = GetConfigString(_config, "startYear", "2000");
    string oldDateSource = GetConfigString(_config, "dateSource", "filename");
    string oldFallbackOrder = GetConfigString(_config, "dateFallbackOrder", "");
    string oldFallbackCustom = GetConfigString(_config, "dateFallbackCustomText", "");
    string oldPreferExif = GetConfigString(_config, "preferExifOnFilenameConflict", "false");
    string oldRealTime = GetConfigString(_config, "realTimePreview", "true");
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
    _suppressEvents = true;
    SyncFallbackCombosFromConfig();
    _suppressEvents = false;

    UpdateChecksumBadge();
    UpdateRootPathText();
    UpdateFallbackUiState();
    UpdateRealTimeUi();

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

    bool realTime = GetConfigBool(_config, "realTimePreview", true);
    bool realTimeChanged = (oldRealTime != _config["realTimePreview"]);
    bool startChanged = (oldStartYear != _config["startYear"]);
    bool dateChanged = (oldDateSource != _config["dateSource"]);
    bool fallbackChanged = (NormalizeFallbackOrder(oldFallbackOrder) != _config["dateFallbackOrder"] ||
        oldFallbackCustom != _config["dateFallbackCustomText"]);
    bool preferExifChanged = (oldPreferExif != _config["preferExifOnFilenameConflict"]);

    if (realTimeChanged && realTime)
    {
        _previewDirty = false;
        RefreshFilenameDates(true);
        return;
    }

    if (!realTime)
    {
        if (realTimeChanged)
        {
            _previewDirty = false;
            RefreshFilenameDates(true);
            return;
        }

        _previewDirty = true;
        if (startChanged)
            RefreshFilenameDates(false);
        else if (dateChanged || fallbackChanged || preferExifChanged)
            RefreshEffectiveDates(false);
        RefreshErrorBanner("");
        UpdateStatus();
        return;
    }

    _previewDirty = false;
    if (startChanged)
        RefreshFilenameDates(true);
    else if (dateChanged || fallbackChanged || preferExifChanged)
        RefreshEffectiveDates(true);
    else
        RecomputeAll();
}

private static void RefreshFilenameDates(bool recomputeTarget)
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

    if (recomputeTarget)
    {
        RecomputeAll();
    }
    else
    {
        for (int i = 0; i < _files.Count; i++)
            RefreshRowDetails(_files[i]);
        if (_grid != null)
            _grid.Items.Refresh();
        UpdateStatus();
    }
}

private static void RefreshEffectiveDates(bool recomputeTarget)
{
    for (int i = 0; i < _files.Count; i++)
        UpdateEffectiveDate(_files[i]);

    if (recomputeTarget)
    {
        RecomputeAll();
    }
    else
    {
        for (int i = 0; i < _files.Count; i++)
            RefreshRowDetails(_files[i]);
        if (_grid != null)
            _grid.Items.Refresh();
        UpdateStatus();
    }
}

private static void UpdateEffectiveDate(Dictionary<string, object> item)
{
    Dictionary<string, DateTime?> dates = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
    dates["filename"] = GetDate(item, "FileNameDate");
    dates["exif"] = GetDate(item, "ExifDate");
    dates["modified"] = GetDate(item, "Modified");
    dates["created"] = GetDate(item, "Created");

    DateTime? effectiveDate;
    string effectiveSource;
    string missingText;
    bool skipped;
    ResolveDateValue(dates, _config["dateSource"], _config["dateFallbackOrder"],
        _config["dateFallbackCustomText"],
        GetConfigBool(_config, "preferExifOnFilenameConflict", false),
        out effectiveDate, out effectiveSource, out missingText, out skipped);

    item["EffectiveDate"] = effectiveDate;
    item["EffectiveSource"] = effectiveSource == null ? "" : effectiveSource;
    item["MissingDateText"] = missingText == null ? "" : missingText;
    item["DateSkipped"] = skipped;
    item["IsSkipped"] = skipped || GetBool(item, "FilterSkipped");
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

    _previewDirty = false;
    UpdateRealTimeUi();
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
        item["MissingDateText"] = "";
        item["DateSkipped"] = false;
        item["FilterSkipped"] = false;
        item["IsSkipped"] = false;
        item["HashHex"] = null;
        item["HashPending"] = false;
        item["TargetPath"] = "";
        item["Action"] = "";
        item["ExecState"] = "";
        item["ExecDetails"] = "";
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
        if (GetBool(_files[i], "IsSkipped"))
            continue;
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

    if (_previewDirty)
    {
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

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
    if (_grid == null || _scanning || _previewDirty)
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
    string filterMode = _config["filterMode"];
    string filterExtensions = _config["filterExtensions"];
    bool hasFilter = filterExtensions != null && filterExtensions.Length > 0;

    // 第一遍：计算每个文件的动作和目标路径。
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
        item["FilterSkipped"] = false;
        item["ExecState"] = "";
        item["ExecDetails"] = "";

        string itemExtension = Convert.ToString(item["Ext"], CultureInfo.InvariantCulture);
        bool extensionMatched = hasFilter && ExtensionMatchesFilter(itemExtension, filterExtensions);
        bool filterSkip = ShouldSkipByFilter(itemExtension, filterExtensions, filterMode);
        item["FilterSkipped"] = filterSkip;
        item["IsSkipped"] = filterSkip || GetBool(item, "DateSkipped");
        item["Action"] = ResolveAction(filterMode, extensionMatched, hasFilter, true);
        if (GetBool(item, "IsSkipped"))
        {
            item["Action"] = "跳过";
            item["Status"] = filterSkip ? "过滤跳过" : "日期跳过";
            RefreshRowDetails(item);
            continue;
        }

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
        string missingDateText = Convert.ToString(item["MissingDateText"], CultureInfo.InvariantCulture);
        string target;
        string error;
        bool ok = TryBuildTargetPath(template, rootDirectory, effectiveDate, missingDateText,
            baseName, extension, checksum, out target, out error);

        if (!ok)
        {
            SetRowError(item, error == null ? "无法生成目标路径" : error, "路径错误");
            continue;
        }

        item["TargetPath"] = target;

        string sourcePath = Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture);
        string sourceDir = Convert.ToString(item["Dir"], CultureInfo.InvariantCulture);
        string targetDir = "";
        try
        {
            targetDir = Path.GetDirectoryName(target);
        }
        catch
        {
            targetDir = "";
        }
        bool sameDirectory = string.Equals(sourceDir == null ? "" : sourceDir.TrimEnd('\\'),
            targetDir == null ? "" : targetDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        string action = ResolveAction(filterMode, extensionMatched, hasFilter, sameDirectory);
        item["Action"] = action;
        if ((action == "复制" || action == "移动") &&
            string.Equals(sourcePath, target, StringComparison.OrdinalIgnoreCase))
        {
            SetRowError(item, "目标路径与源路径相同，无法执行" + action, "路径错误");
            continue;
        }

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
        if (GetBool(item, "HasError") || GetBool(item, "IsPending") || GetBool(item, "IsSkipped"))
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
        if (GetBool(item, "HasError") || GetBool(item, "IsPending") || GetBool(item, "IsSkipped"))
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
    RefreshErrorBanner(templateValid ? null :
        (badToken == null || badToken.Length == 0
            ? "目标路径模板为空" : "模板包含未知胶囊：" + badToken));

    int statusPending = CountPendingRows();
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
    if (!GetDate(item, "EffectiveDate").HasValue)
    {
        string replacement = Convert.ToString(item["MissingDateText"], CultureInfo.InvariantCulture);
        builder.AppendLine("日期缺失替换值：" + (replacement == null || replacement.Length == 0 ? "（空值）" : replacement));
    }
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
    builder.AppendLine("动作：" + Convert.ToString(item["Action"], CultureInfo.InvariantCulture));
    string execDetails = Convert.ToString(item["ExecDetails"], CultureInfo.InvariantCulture);
    if (execDetails != null && execDetails.Length > 0)
        builder.AppendLine("执行结果：" + execDetails);
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
    if (source == "empty") return "空值";
    if (source == "custom") return "自定义值";
    if (source == "skip") return "跳过";
    return "—";
}

private static void UpdateStatus()
{
    int ready;
    int errors;
    int pending;
    CountRows(out ready, out errors, out pending);

    bool realTime = GetConfigBool(_config, "realTimePreview", true);
    _previewButton.IsEnabled = (!realTime && !_scanning);
    if (_executeButton != null)
        _executeButton.IsEnabled = false;
    if (_finishButton != null)
        _finishButton.IsEnabled = (!_scanning && !_executing);

    if (_executing)
        return;

    if (_previewDirty && !_scanning)
    {
        _statusText.Text = "配置已变更，点击【预览】刷新目标路径";
        SetApplyEnabled(false);
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

    if (_scanning)
    {
        _statusText.Text = "正在读取文件属性… 已加载 " + _files.Count + " 个文件";
        SetApplyEnabled(false);
        return;
    }

    if (_files.Count == 0)
    {
        _statusText.Text = "没有可处理的文件" + DescribeSkipped();
        SetApplyEnabled(false);
        _progress.Visibility = Visibility.Collapsed;
        return;
    }

    string text = "共 " + _files.Count + " 个文件" + DescribeSkipped();
    text += "；就绪 " + ready + "；异常 " + errors;
    int skipped = CountSkippedRows();
    if (skipped > 0)
        text += "；过滤/日期跳过 " + skipped;
    if (pending > 0)
        text += "；校验码计算中 " + pending;
    if (TemplateContainsToken(_config["template"], "【校验码】") && _hashTotal > 0 &&
        _hashDone < _hashTotal)
        text += "（" + _hashDone + "/" + _hashTotal + "）";
    _statusText.Text = text;

    SetApplyEnabled(!_scanning && pending == 0 && ready > 0);
    if (_executeButton != null)
        _executeButton.IsEnabled = (!_scanning && pending == 0 && ready > 0 && !_executing);
    if (pending > 0 && TemplateContainsToken(_config["template"], "【校验码】"))
    {
        _progress.Visibility = Visibility.Visible;
        if (_hashTotal > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Maximum = _hashTotal;
            _progress.Value = _hashDone;
        }
        else
        {
            _progress.IsIndeterminate = true;
        }
    }
    else
    {
        _progress.Visibility = Visibility.Collapsed;
    }
}

private static void RefreshErrorBanner(string globalError)
{
    if (_errorBanner == null || _errorBannerText == null)
        return;

    if (_previewDirty)
    {
        _errorBanner.Visibility = Visibility.Collapsed;
        return;
    }

    List<string> messageOrder = new List<string>();
    Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
    int errorRows = 0;

    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (!GetBool(item, "HasError") || GetBool(item, "IsSkipped"))
            continue;
        errorRows++;
        string message = Convert.ToString(item["Error"], CultureInfo.InvariantCulture);
        if (message == null || message.Length == 0)
            message = Convert.ToString(item["Status"], CultureInfo.InvariantCulture);
        if (message == null || message.Length == 0)
            message = "未知错误";
        int count;
        counts.TryGetValue(message, out count);
        if (count == 0)
            messageOrder.Add(message);
        counts[message] = count + 1;
    }

    if (errorRows == 0 && globalError != null && globalError.Length > 0)
    {
        messageOrder.Add(globalError);
        counts[globalError] = 1;
    }

    if (messageOrder.Count == 0)
    {
        _errorBannerText.Text = "";
        _errorBanner.Visibility = Visibility.Collapsed;
        return;
    }

    StringBuilder builder = new StringBuilder();
    if (errorRows > 0)
        builder.Append("发现 " + errorRows + " 个异常路径，请在表格中查看红底高亮行。");
    else
        builder.Append("发现路径配置错误。");

    int shown = 0;
    for (int i = 0; i < messageOrder.Count && shown < 5; i++)
    {
        string message = messageOrder[i];
        int count = counts[message];
        builder.Append("\n• " + message);
        if (count > 1)
            builder.Append("（" + count + " 个）");
        shown++;
    }
    if (messageOrder.Count > shown)
        builder.Append("\n• …");

    _errorBannerText.Text = builder.ToString();
    _errorBanner.Visibility = Visibility.Visible;
}

private static int CountPendingRows()
{
    int ready;
    int errors;
    int pending;
    CountRows(out ready, out errors, out pending);
    return pending;
}

private static int CountSkippedRows()
{
    int count = 0;
    for (int i = 0; i < _files.Count; i++)
    {
        if (GetBool(_files[i], "IsSkipped"))
            count++;
    }
    return count;
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

private static void Preview_Click(object sender, RoutedEventArgs e)
{
    if (_scanning)
    {
        MessageBox.Show("文件属性仍在读取中，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }
    RefreshPreviewNow();
}

private static void RefreshPreviewNow()
{
    if (_scanning || _config == null)
        return;

    if (_debounceTimer != null)
        _debounceTimer.Stop();

    string oldAlgorithm = GetConfigString(_config, "checksumAlgorithm", "CRC32");
    int oldDigits = GetConfigInt(_config, "checksumDigits", 8);
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

    _suppressEvents = true;
    SyncFallbackCombosFromConfig();
    _suppressEvents = false;

    UpdateChecksumBadge();
    UpdateRootPathText();
    UpdateFallbackUiState();
    UpdateRealTimeUi();
    if (!_digitsText.IsKeyboardFocusWithin)
        _digitsText.Text = _config["checksumDigits"];

    _previewDirty = false;
    RefreshFilenameDates(true);
}

private static void LoadPreset_Click(object sender, RoutedEventArgs e)
{
    if (_presetPopup == null || _presetListBox == null)
        return;

    Dictionary<string, Dictionary<string, object>> store = LoadPresetStore();
    if (store.Count == 0)
    {
        MessageBox.Show("当前没有已保存的预设。", "载入预设",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    List<string> names = new List<string>(store.Keys);
    names.Sort(StringComparer.OrdinalIgnoreCase);

    _suppressPresetSelection = true;
    _presetListBox.Items.Clear();
    for (int i = 0; i < names.Count; i++)
        _presetListBox.Items.Add(names[i]);
    _presetListBox.SelectedItem = null;
    _suppressPresetSelection = false;

    if (_loadPresetButton != null)
        _presetPopup.PlacementTarget = _loadPresetButton;
    _presetPopup.Placement = PlacementMode.Bottom;
    _presetPopup.IsOpen = true;
}

private static void PresetListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_suppressPresetSelection)
        return;

    string name = _presetListBox.SelectedItem as string;
    if (name == null)
        return;

    _presetPopup.IsOpen = false;
    _suppressPresetSelection = true;
    _presetListBox.SelectedItem = null;
    _suppressPresetSelection = false;
    LoadPresetByName(name);
}

private static void LoadPresetByName(string name)
{
    Dictionary<string, Dictionary<string, object>> store = LoadPresetStore();
    string actualName = FindPresetKey(store, name);
    if (actualName == null)
    {
        MessageBox.Show("未找到预设：" + name, "载入预设",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (!ApplyPresetDictionary(store[actualName], _config))
    {
        MessageBox.Show("预设中没有可识别的配置项。", "载入预设",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (_debounceTimer != null)
        _debounceTimer.Stop();
    SyncUiFromConfig();
    if (_scanning)
    {
        _statusText.Text = "已载入预设“" + actualName + "”，文件属性读取完成后刷新预览";
        return;
    }
    _previewDirty = false;
    RefreshFilenameDates(true);
    _statusText.Text = "已载入预设：" + actualName;
}

private static void SavePreset_Click(object sender, RoutedEventArgs e)
{
    if (_debounceTimer != null)
        _debounceTimer.Stop();

    ReadConfigFromUi();
    NormalizeConfig(_config);
    _suppressEvents = true;
    SyncFallbackCombosFromConfig();
    _suppressEvents = false;

    string name = PromptForPresetName("");
    if (name == null)
        return;
    name = name.Trim();
    if (name.Length == 0)
        return;

    Dictionary<string, Dictionary<string, object>> store = LoadPresetStore();
    string existingName = FindPresetKey(store, name);
    Dictionary<string, object> newConfig = BuildPresetDictionary(_config);

    if (existingName != null)
    {
        Dictionary<string, string> oldConfig = CreateDefaultConfig(GetInitialStartYear());
        ApplyPresetDictionary(store[existingName], oldConfig);
        string diff = BuildPresetDiff(oldConfig, _config);
        if (diff.Length == 0)
        {
            // 配置未变化也回写一次，顺带把旧嵌套结构迁移成扁平结构。
            SavePresetStore(store);
            _statusText.Text = "预设“" + existingName + "”与当前配置一致，无需覆盖";
            return;
        }

        DialogResult answer = MessageBox.Show(
            "预设“" + existingName + "”已存在。\n\n以下配置将变更：\n" + diff +
            "\n是否覆盖？",
            "保存预设", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;

        store[existingName] = newConfig;
        SavePresetStore(store);
        _statusText.Text = "已覆盖预设：" + existingName;
        return;
    }

    store[name] = newConfig;
    SavePresetStore(store);
    _statusText.Text = "已保存预设：" + name;
}

private static void LoadExtPreset_Click(object sender, RoutedEventArgs e)
{
    Dictionary<string, string> store = LoadExtensionPresetStore();
    if (store.Count == 0)
    {
        MessageBox.Show("当前没有已保存的扩展名预设。", "载入扩展名预设",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    List<string> names = new List<string>(store.Keys);
    names.Sort(StringComparer.OrdinalIgnoreCase);

    ContextMenu menu = new ContextMenu();
    for (int i = 0; i < names.Count; i++)
    {
        MenuItem item = new MenuItem();
        item.Header = names[i];
        item.Tag = names[i];
        item.Click += ExtPresetItem_Click;
        menu.Items.Add(item);
    }
    menu.PlacementTarget = _loadExtPresetButton;
    menu.Placement = PlacementMode.Bottom;
    menu.IsOpen = true;
}

private static void ExtPresetItem_Click(object sender, RoutedEventArgs e)
{
    MenuItem item = sender as MenuItem;
    if (item == null || item.Tag == null)
        return;
    LoadExtensionPreset(Convert.ToString(item.Tag, CultureInfo.InvariantCulture));
}

private static void LoadExtensionPreset(string name)
{
    Dictionary<string, string> store = LoadExtensionPresetStore();
    string actualName = FindExtensionPresetKey(store, name);
    if (actualName == null)
        return;

    DialogResult answer = MessageBox.Show(
        "载入扩展名预设“" + actualName + "”。\n\n是：覆盖现有扩展名列表\n否：追加到现有列表\n取消：放弃载入",
        "载入扩展名预设", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
    if (answer == DialogResult.Cancel)
        return;

    string value = store[actualName];
    if (answer == DialogResult.Yes)
        _filterExtensionsText.Text = value;
    else
        _filterExtensionsText.Text = NormalizeExtensionList(
            (_filterExtensionsText.Text == null ? "" : _filterExtensionsText.Text) + ";" + value);

    if (_debounceTimer != null)
        _debounceTimer.Stop();
    ApplyUiConfig();
    _statusText.Text = "已" + (answer == DialogResult.Yes ? "覆盖载入" : "追加载入") +
        "扩展名预设：" + actualName;
}

private static void SaveExtPreset_Click(object sender, RoutedEventArgs e)
{
    string list = NormalizeExtensionList(_filterExtensionsText.Text);
    if (list.Length == 0)
    {
        MessageBox.Show("扩展名列表为空，无法保存。", "保存扩展名预设",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    string name = PromptForPresetName("");
    if (name == null)
        return;
    name = name.Trim();
    if (name.Length == 0)
        return;

    Dictionary<string, string> store = LoadExtensionPresetStore();
    string existingName = FindExtensionPresetKey(store, name);
    if (existingName != null)
    {
        DialogResult answer = MessageBox.Show(
            "扩展名预设“" + existingName + "”已存在。\n\n旧值：" + store[existingName] +
            "\n新值：" + list + "\n\n是否覆盖？",
            "保存扩展名预设", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;
        store[existingName] = list;
        name = existingName;
    }
    else
    {
        store[name] = list;
    }

    SaveExtensionPresetStore(store);
    _statusText.Text = "已保存扩展名预设：" + name;
}

private static Dictionary<string, string> LoadExtensionPresetStore()
{
    if (_data == null)
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    object presetObject;
    if (!_data.TryGetValue("extensionPresets", out presetObject) || presetObject == null)
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    return ParseExtensionPresetDictionary(presetObject);
}

private static void SaveExtensionPresetStore(Dictionary<string, string> store)
{
    if (_data == null)
        return;

    // 与主预设一致：key为预设名，value为扩展名列表字符串，避免嵌套词典状态退化。
    Dictionary<string, object> flat = BuildFlatExtensionPresetDictionary(store);
    _data["extensionPresets"] = flat;

    PublishActionVariable("扩展名预设", flat);
}

private static string FindExtensionPresetKey(Dictionary<string, string> store, string name)
{
    foreach (string key in store.Keys)
    {
        if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            return key;
    }
    return null;
}

private static Dictionary<string, Dictionary<string, object>> LoadPresetStore()
{
    if (_data == null)
        return new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

    object presetObject;
    if (!_data.TryGetValue("preset", out presetObject) || presetObject == null)
        return new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

    return ParsePresetDictionary(presetObject);
}

private static void SavePresetStore(Dictionary<string, Dictionary<string, object>> store)
{
    if (_data == null)
        return;

    // 统一为“预设名 -> 配置JSON字符串”的扁平词典，避免嵌套词典状态读回退化。
    Dictionary<string, object> flat = BuildFlatPresetDictionary(store);
    _data["preset"] = flat;

    // 尽量实时同步到动作变量；若当前Quicker版本未暴露Context，则仍会在窗口关闭时写回。
    PublishActionVariable("批量重命名器预设", flat);
}

private static void PrimePresetVariables()
{
    if (_data == null)
        return;

    object presetValue;
    bool presetMissing = !_data.TryGetValue("preset", out presetValue) ||
        IsVariableMissing(presetValue);
    if (presetMissing && TryGetActionVariable("批量重命名器预设", out presetValue) &&
        !IsVariableMissing(presetValue))
        _data["preset"] = presetValue;

    object extValue;
    bool extMissing = !_data.TryGetValue("extensionPresets", out extValue) ||
        IsVariableMissing(extValue);
    if (extMissing && TryGetActionVariable("扩展名预设", out extValue) &&
        !IsVariableMissing(extValue))
        _data["extensionPresets"] = extValue;

    // Quicker“保存为状态”会把嵌套词典序列化成JSON文本，下次启动时变量可能是string。
    // 在窗口数据层先还原成词典，后续载入、保存和关闭写回都使用词典对象。
    NormalizePresetVariable("preset");
    NormalizePresetVariable("extensionPresets");
}

// 数据映射可能把未赋值的词典变量传成空词典或空字符串；此时应回退读取动作变量。
private static bool IsVariableMissing(object value)
{
    if (value == null)
        return true;

    string text = value as string;
    if (text != null)
        return text.Trim().Length == 0;

    IDictionary dictionary = value as IDictionary;
    if (dictionary != null)
        return dictionary.Count == 0;

    return false;
}

private static void NormalizePresetVariable(string key)
{
    if (_data == null || key == null)
        return;

    object value;
    if (!_data.TryGetValue(key, out value) || value == null)
        return;
    if (value is IDictionary<string, object> || value is IDictionary)
        return;

    string text = value as string;
    if (text == null)
        return;

    IDictionary<string, object> parsed = AsObjectDictionary(text);
    if (parsed != null)
        _data[key] = parsed;
}

private static bool TryGetActionVariable(string variableName, out object value)
{
    value = null;
    if (_winContext == null || variableName == null)
        return false;
    try
    {
        object target = _winContext;
        if (TryGetVarValue(target, variableName, out value))
            return true;

        PropertyInfo contextProperty = target.GetType().GetProperty("Context");
        if (contextProperty == null)
            return false;
        object context = contextProperty.GetValue(target, null);
        if (context == null)
            return false;
        if (TryGetVarValue(context, variableName, out value))
            return true;

        MethodInfo rootMethod = context.GetType().GetMethod("GetRootContext", Type.EmptyTypes);
        if (rootMethod == null)
            return false;
        object root = rootMethod.Invoke(context, null);
        return TryGetVarValue(root, variableName, out value);
    }
    catch
    {
        return false;
    }
}

private static bool TryGetVarValue(object target, string variableName, out object value)
{
    value = null;
    MethodInfo method = FindVariableMethod(target, "GetVarValue",
        new Type[] { typeof(string) });
    if (method == null)
        return false;
    value = method.Invoke(target, new object[] { variableName });
    return true;
}

private static MethodInfo FindVariableMethod(object target, string methodName, Type[] parameterTypes)
{
    if (target == null || methodName == null)
        return null;

    Type type = target.GetType();
    MethodInfo method = type.GetMethod(methodName, parameterTypes);
    if (method != null)
        return method;

    // Quicker上下文可能以显式接口实现提供GetVarValue/SetVarValue，
    // 此时必须从接口上取方法再对实例调用。
    foreach (Type iface in type.GetInterfaces())
    {
        method = iface.GetMethod(methodName, parameterTypes);
        if (method != null)
            return method;
    }
    return null;
}

private static void PublishActionVariable(string variableName, object value)
{
    if (_winContext == null || variableName == null)
        return;
    // 不同版本可能直接提供 SetVarValue，或通过 Context / GetRootContext 暴露。
    // 逐级尝试所有可用上下文，避免只写入步骤局部作用域而动作根变量仍是旧值。
    TryPublishTo(_winContext, variableName, value);

    try
    {
        PropertyInfo contextProperty = _winContext.GetType().GetProperty("Context");
        if (contextProperty == null)
            return;

        object context = contextProperty.GetValue(_winContext, null);
        if (context == null)
            return;

        TryPublishTo(context, variableName, value);

        MethodInfo rootMethod = context.GetType().GetMethod("GetRootContext", Type.EmptyTypes);
        if (rootMethod != null)
        {
            object root = rootMethod.Invoke(context, null);
            TryPublishTo(root, variableName, value);
        }
    }
    catch
    {
        // 实时写变量失败不影响窗口内保存结果，关闭时由数据映射写回。
    }
}

private static bool TryPublishTo(object target, string variableName, object value)
{
    try
    {
        return TrySetVarValue(target, variableName, value);
    }
    catch
    {
        return false;
    }
}

private static bool TrySetVarValue(object target, string variableName, object value)
{
    if (target == null)
        return false;

    MethodInfo method = FindVariableMethod(target, "SetVarValue",
        new Type[] { typeof(string), typeof(object) });
    if (method == null)
    {
        // 兼容个别版本使用不同第二参数类型的情况。
        List<Type> types = new List<Type>();
        types.Add(target.GetType());
        types.AddRange(target.GetType().GetInterfaces());
        foreach (Type type in types)
        {
            foreach (MethodInfo candidate in type.GetMethods())
            {
                if (candidate.Name != "SetVarValue")
                    continue;
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != 2)
                    continue;
                if (parameters[0].ParameterType != typeof(string))
                    continue;
                if (!parameters[1].ParameterType.IsInstanceOfType(value) &&
                    parameters[1].ParameterType != typeof(object))
                    continue;
                method = candidate;
                break;
            }
            if (method != null)
                break;
        }
    }
    if (method == null)
        return false;

    method.Invoke(target, new object[] { variableName, value });
    return true;
}

private static IDictionary<string, object> AsObjectDictionary(object value)
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

    // “保存为状态”的嵌套词典可能以JSON文本读回，这里统一还原为词典。
    string text = value as string;
    if (text == null)
        return null;

    object parsed;
    if (!TryParseJson(text, out parsed))
        return null;
    return AsObjectDictionary(parsed);
}

private static string FindPresetKey(Dictionary<string, Dictionary<string, object>> store, string name)
{
    foreach (string key in store.Keys)
    {
        if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            return key;
    }
    return null;
}

private static string BuildPresetDiff(Dictionary<string, string> oldConfig,
    Dictionary<string, string> newConfig)
{
    string[] keys = new string[] {
        "rootMode", "rootPath", "startYear",
        "filterExtensions", "filterMode",
        "dateSource", "dateFallbackOrder", "dateFallbackCustomText",
        "preferExifOnFilenameConflict",
        "realTimePreview", "checksumAlgorithm", "checksumDigits",
        "checksumPrefix", "checksumSuffix", "template"
    };

    StringBuilder builder = new StringBuilder();
    for (int i = 0; i < keys.Length; i++)
    {
        string key = keys[i];
        string oldValue = GetConfigString(oldConfig, key, "");
        string newValue = GetConfigString(newConfig, key, "");
        if (oldValue == newValue)
            continue;
        builder.AppendLine("• " + GetConfigLabel(key) + "：" +
            FormatPresetValue(key, oldValue) + " → " + FormatPresetValue(key, newValue));
    }
    return builder.ToString();
}

private static string GetConfigLabel(string key)
{
    if (key == "rootMode") return "根路径模式";
    if (key == "rootPath") return "根路径";
    if (key == "startYear") return "最早起始年份";
    if (key == "filterExtensions") return "过滤器扩展名";
    if (key == "filterMode") return "过滤器决策模式";
    if (key == "dateSource") return "日期来源";
    if (key == "dateFallbackOrder") return "递补顺序";
    if (key == "dateFallbackCustomText") return "递补自定义值";
    if (key == "preferExifOnFilenameConflict") return "文件名日期与EXIF不一致时用EXIF";
    if (key == "realTimePreview") return "实时刷新";
    if (key == "checksumAlgorithm") return "校验算法";
    if (key == "checksumDigits") return "校验位数";
    if (key == "checksumPrefix") return "校验码前缀";
    if (key == "checksumSuffix") return "校验码后缀";
    if (key == "template") return "目标路径模板";
    return key;
}

private static string FormatPresetValue(string key, string value)
{
    if (value == null)
        value = "";
    if (key == "rootMode")
        return value == "custom" ? "其他文件夹" : "当前文件夹";
    if (key == "dateSource")
        return GetDateSourceName(value);
    if (key == "filterMode")
        return value == "execute" ? "匹配时执行（白名单）" : "匹配时跳过";
    if (key == "realTimePreview")
        return value == "true" ? "开启" : "关闭";
    if (key == "preferExifOnFilenameConflict")
        return value == "true" ? "开启" : "关闭";
    if (key == "dateFallbackOrder")
        return FormatDateOrder(value);
    if (value.Length == 0)
        return "（空）";
    string text = value.Replace("\r", " ").Replace("\n", " ");
    if (text.Length > 90)
        text = text.Substring(0, 90) + "…";
    return text;
}

private static string FormatDateOrder(string order)
{
    string[] parts = (order == null ? "" : order).Split(',');
    StringBuilder builder = new StringBuilder();
    for (int i = 0; i < parts.Length; i++)
    {
        string part = parts[i] == null ? "" : parts[i].Trim();
        if (part.Length == 0)
            continue;
        if (builder.Length > 0)
            builder.Append(" → ");
        builder.Append(GetDateSourceName(part));
    }
    return builder.Length == 0 ? "（空）" : builder.ToString();
}

private static string PromptForPresetName(string initialName)
{
    Window dialog = new Window();
    dialog.Title = "保存预设";
    dialog.Width = 400;
    dialog.Height = 180;
    dialog.ResizeMode = ResizeMode.NoResize;
    dialog.ShowInTaskbar = false;
    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    dialog.Background = Brushes.White;
    dialog.FontSize = 12;
    if (_window != null)
        dialog.Owner = _window;

    Grid panel = new Grid();
    panel.Margin = new Thickness(16);
    panel.RowDefinitions.Add(new RowDefinition());
    panel.RowDefinitions.Add(new RowDefinition());
    panel.RowDefinitions.Add(new RowDefinition());

    TextBlock label = new TextBlock();
    label.Text = "预设名称：";
    label.Foreground = new SolidColorBrush(Color.FromRgb(52, 64, 84));
    panel.Children.Add(label);

    TextBox nameBox = new TextBox();
    nameBox.Height = 28;
    nameBox.VerticalContentAlignment = VerticalAlignment.Center;
    nameBox.Padding = new Thickness(6, 0, 6, 0);
    nameBox.Text = initialName == null ? "" : initialName;
    nameBox.Margin = new Thickness(0, 8, 0, 12);
    Grid.SetRow(nameBox, 1);
    panel.Children.Add(nameBox);

    StackPanel buttons = new StackPanel();
    buttons.Orientation = Orientation.Horizontal;
    buttons.HorizontalAlignment = HorizontalAlignment.Right;
    Grid.SetRow(buttons, 2);
    panel.Children.Add(buttons);

    Button cancel = new Button();
    cancel.Content = "取消";
    cancel.Width = 80;
    cancel.Height = 28;
    cancel.Margin = new Thickness(0, 0, 8, 0);
    cancel.IsCancel = true;
    buttons.Children.Add(cancel);

    Button ok = new Button();
    ok.Content = "保存";
    ok.Width = 80;
    ok.Height = 28;
    ok.IsDefault = true;
    buttons.Children.Add(ok);

    ok.Click += delegate
    {
        if (nameBox.Text == null || nameBox.Text.Trim().Length == 0)
        {
            MessageBox.Show("请输入预设名称。", "保存预设",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            nameBox.Focus();
            return;
        }
        dialog.DialogResult = true;
    };
    cancel.Click += delegate
    {
        dialog.DialogResult = false;
    };

    dialog.Content = panel;
    dialog.Loaded += delegate
    {
        nameBox.Focus();
        nameBox.SelectAll();
    };

    bool? result = dialog.ShowDialog();
    if (result == true)
        return nameBox.Text.Trim();
    return null;
}

private static void Apply_Click(object sender, RoutedEventArgs e)
{
    if (DeliveryMode == 2)
        return;

    if (_scanning)
    {
        MessageBox.Show("正在读取文件属性，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    // 立即吸收尚未到防抖时间的文本修改，并刷新预览，避免输出旧配置。
    if (_debounceTimer != null)
        _debounceTimer.Stop();
    RefreshPreviewNow();

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

    WriteOutputs(BuildReadyOutputRows());
    EnsureDefaultPreset();

    if (_window != null)
        _window.Close();
}

private static void Finish_Click(object sender, RoutedEventArgs e)
{
    if (_executing)
        return;

    if (DeliveryMode == 2)
    {
        // 执行方案不生成/输出列表变量，只写回预设并继续 Quicker 后续步骤。
        EnsureDefaultPreset();
        if (_window != null)
            _window.Close();
        return;
    }

    if (_scanning)
    {
        MessageBox.Show("正在读取文件属性，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (_debounceTimer != null)
        _debounceTimer.Stop();
    RefreshPreviewNow();

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

    WriteOutputs(BuildReadyOutputRows());
    EnsureDefaultPreset();

    if (_window != null)
        _window.Close();
}

private static List<Dictionary<string, object>> BuildReadyOutputRows()
{
    List<Dictionary<string, object>> outputRows = new List<Dictionary<string, object>>();
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError") || GetBool(item, "IsPending") || GetBool(item, "IsSkipped"))
            continue;
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (target == null || target.Length == 0)
            continue;
        outputRows.Add(item);
    }
    return outputRows;
}

private static void Cancel_Click(object sender, RoutedEventArgs e)
{
    if (_window != null)
        _window.Close();
}

// ---------------------------------------------------------------------------
// 执行：复制 / 移动 / 重命名
// ---------------------------------------------------------------------------

private static void Execute_Click(object sender, RoutedEventArgs e)
{
    if (DeliveryMode == 1)
        return;
    if (_executing)
        return;

    if (_scanning)
    {
        MessageBox.Show("正在读取文件属性，请稍候。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    if (_debounceTimer != null)
        _debounceTimer.Stop();
    RefreshPreviewNow();

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
        MessageBox.Show("没有可执行的文件，请检查过滤器、模板与文件状态。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }
    if (errors > 0)
    {
        DialogResult answer = MessageBox.Show(
            "有 " + errors + " 个文件存在异常，不会执行；将执行 " + ready +
            " 个就绪文件。是否继续？", "批量重命名器",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
            return;
    }

    List<Dictionary<string, object>> queue = new List<Dictionary<string, object>>();
    for (int i = 0; i < _files.Count; i++)
    {
        Dictionary<string, object> item = _files[i];
        if (GetBool(item, "HasError") || GetBool(item, "IsPending") || GetBool(item, "IsSkipped"))
            continue;
        string action = Convert.ToString(item["Action"], CultureInfo.InvariantCulture);
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (action == null || action.Length == 0 || action == "跳过" ||
            target == null || target.Length == 0)
            continue;
        item["ExecState"] = "";
        item["ExecDetails"] = "";
        queue.Add(item);
    }

    if (queue.Count == 0)
    {
        MessageBox.Show("没有可执行的文件，请检查过滤器、模板与文件状态。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    // 执行前先写出非跳过列表与动作，窗口中途关闭时数据映射仍有有效输出。
    WriteOutputs(queue);
    EnsureDefaultPreset();

    int parallel = NormalizeParallelCount(GetConfigInt(_config, "execParallel", 2));
    _execQueue = queue;
    _execTotal = queue.Count;
    _execCursor = 0;
    _execDone = 0;
    _execErrors = 0;
    _execBytes = 0;
    _execWorkers = parallel;
    _execStop = false;
    _executing = true;
    _execStartTicks = DateTime.UtcNow.Ticks;

    SetExecutionUiEnabled(false);
    if (_execProgress != null)
    {
        _execProgress.Maximum = Math.Max(1, _execTotal);
        _execProgress.Value = 0;
        _execProgress.Visibility = Visibility.Visible;
    }
    UpdateExecutionStatus();
    StartExecutionTimer();

    for (int i = 0; i < parallel; i++)
    {
        Thread thread = new Thread(ExecutionWorker);
        thread.IsBackground = true;
        thread.Start();
    }
}

private static void ExecutionWorker()
{
    try
    {
        while (!_execStop)
        {
            int index = Interlocked.Increment(ref _execCursor) - 1;
            if (index >= _execQueue.Count)
                break;
            ExecuteOneFile(_execQueue[index]);
        }
    }
    finally
    {
        int remaining = Interlocked.Decrement(ref _execWorkers);
        if (remaining == 0)
            PostExecutionCompleted();
    }
}

private static void ExecuteOneFile(Dictionary<string, object> item)
{
    string action = Convert.ToString(item["Action"], CultureInfo.InvariantCulture);
    string source = Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture);
    string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);

    try
    {
        if (action == null || action.Length == 0 || action == "跳过")
            return;
        if (!File.Exists(source))
            throw new IOException("源文件不存在");
        if (target == null || target.Length == 0)
            throw new IOException("目标路径为空");

        string targetDirectory = Path.GetDirectoryName(target);
        if (targetDirectory == null || targetDirectory.Length == 0)
            throw new IOException("目标路径缺少目录");
        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            if (action == "重命名")
            {
                Interlocked.Increment(ref _execDone);
                ReportExecutionRow(item, true, "已完成（目标与源相同，无需操作）", "已完成");
                return;
            }
            throw new IOException("目标路径与源路径相同");
        }
        if (File.Exists(target))
            throw new IOException("目标文件已存在");

        bool verify = GetConfigBool(_config, "execVerify", true);
        string verificationSourceHash = null;
        if (verify && (action == "复制" || action == "移动"))
        {
            verificationSourceHash = Convert.ToString(item["HashHex"], CultureInfo.InvariantCulture);
            if (verificationSourceHash == null || verificationSourceHash.Length == 0)
            {
                string hashError;
                verificationSourceHash = ComputeHashHex(source, _config["checksumAlgorithm"], out hashError);
                if (hashError != null)
                    throw new IOException("执行后校验准备失败：无法计算源文件校验码（" + hashError + "）");
            }
        }

        Action<long> progress = delegate(long delta)
        {
            if (delta != 0)
                Interlocked.Add(ref _execBytes, delta);
        };

        if (action == "复制")
        {
            string copyError;
            if (!CopyFileWithRetry(source, target, progress, null, 3, out copyError))
                throw new IOException(copyError);
            try
            {
                if (verify)
                    VerifyExecutedFile(target, verificationSourceHash);
            }
            catch
            {
                // 复制后校验失败时删除未通过校验的目标文件；源文件保持不变。
                TryDeleteTargetFile(target);
                throw;
            }
        }
        else if (action == "移动")
        {
            bool crossVolume = !IsSameVolumePath(source, target);
            if (crossVolume)
            {
                string copyError;
                if (!CopyFileWithRetry(source, target, progress, null, 3, out copyError))
                    throw new IOException(copyError);
                try
                {
                    if (verify)
                        VerifyExecutedFile(target, verificationSourceHash);
                }
                catch
                {
                    // 跨卷移动在删除源文件前校验；失败时删除目标，保留源文件。
                    TryDeleteTargetFile(target);
                    throw;
                }
                try
                {
                    File.Delete(source);
                }
                catch
                {
                    TryDeleteTargetFile(target);
                    throw new IOException("跨卷移动失败：无法删除源文件");
                }
            }
            else
            {
                string moveError;
                if (!MoveFileWithRetry(source, target, 3, out moveError))
                    throw new IOException(moveError);
                if (verify)
                    VerifyExecutedFile(target, verificationSourceHash);
            }
        }
        else if (action == "重命名")
        {
            string moveError;
            if (!MoveFileWithRetry(source, target, 3, out moveError))
                throw new IOException(moveError);
        }
        else
        {
            throw new IOException("未知动作：" + action);
        }

        Interlocked.Increment(ref _execDone);
        ReportExecutionRow(item, true, action + "完成" +
            (verify && (action == "复制" || action == "移动") ? "，校验通过" : ""), "已完成");
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref _execErrors);
        ReportExecutionRow(item, false, ex.Message, "执行失败");
    }
}

private static void VerifyExecutedFile(string target, string expectedHash)
{
    string error;
    string targetHash = ComputeHashHex(target, _config["checksumAlgorithm"], out error);
    if (error != null)
        throw new IOException("执行后校验失败：无法计算目标文件校验码（" + error + "）");
    if (!string.Equals(expectedHash, targetHash, StringComparison.OrdinalIgnoreCase))
        throw new IOException("执行后校验失败：目标文件校验码与源文件不一致");

    if (!TemplateContainsToken(_config["template"], "【校验码】"))
        return;

    string badge = BuildChecksumBadge(_config["checksumAlgorithm"],
        GetConfigInt(_config, "checksumDigits", 8),
        _config["checksumPrefix"], _config["checksumSuffix"], expectedHash);
    string fileName = Path.GetFileName(target);
    if (fileName == null || fileName.IndexOf(badge, StringComparison.OrdinalIgnoreCase) < 0)
        throw new IOException("执行后校验失败：文件名中未找到校验码 " + badge);
}

private static void TryDeleteTargetFile(string path)
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

private static void ReportExecutionRow(Dictionary<string, object> item, bool success,
    string message, string status)
{
    if (_window == null)
        return;
    try
    {
        _window.Dispatcher.BeginInvoke(new Action(delegate()
        {
            if (_window == null || _config == null)
                return;
            lock (_execRowLock)
            {
                item["ExecState"] = success ? "done" : "error";
                item["ExecDetails"] = message == null ? "" : message;
                item["Status"] = status;
                if (success)
                {
                    item["HasError"] = false;
                    item["IsPending"] = false;
                    item["Error"] = "";
                }
                else
                {
                    item["HasError"] = true;
                    item["IsPending"] = false;
                    item["Error"] = message == null ? "执行失败" : message;
                }
                RefreshRowDetails(item);
                if (_grid != null)
                    _grid.Items.Refresh();
                UpdateExecutionStatus();
            }
        }));
    }
    catch
    {
        // 窗口已关闭时忽略界面更新。
    }
}

private static void StartExecutionTimer()
{
    StopExecutionTimer();
    _execTimer = new DispatcherTimer();
    _execTimer.Interval = TimeSpan.FromMilliseconds(200);
    _execTimer.Tick += ExecutionTimer_Tick;
    _execTimer.Start();
}

private static void StopExecutionTimer()
{
    if (_execTimer != null)
    {
        _execTimer.Stop();
        _execTimer = null;
    }
}

private static void ExecutionTimer_Tick(object sender, EventArgs e)
{
    UpdateExecutionStatus();
}

private static void UpdateExecutionStatus()
{
    if (_execStatusText == null)
        return;

    int total = _execTotal;
    int done = _execDone;
    int errors = _execErrors;
    int finished = done + errors;

    long bytes = Interlocked.Read(ref _execBytes);
    long startTicks = _execStartTicks;
    double seconds = startTicks > 0
        ? (DateTime.UtcNow.Ticks - startTicks) / (double)TimeSpan.TicksPerSecond : 0;
    if (seconds < 0.001)
        seconds = 0.001;

    string transfer = bytes > 0 ? FormatSize((long)(bytes / seconds)) + "/s" : "—";
    string speed = (finished / seconds).ToString("0.0", CultureInfo.CurrentCulture) + " 个/s";

    _execStatusText.Text = "执行总数 " + total + "；已完成 " + done + "；错误 " + errors +
        "；传输速度 " + transfer + "；执行速度 " + speed;

    if (_execProgress != null)
    {
        _execProgress.Maximum = Math.Max(1, total);
        _execProgress.Value = finished;
        _execProgress.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

private static void PostExecutionCompleted()
{
    if (_window == null)
        return;
    try
    {
        _window.Dispatcher.BeginInvoke(new Action(delegate()
        {
            if (_window == null)
                return;
            _executing = false;
            _execStop = false;
            StopExecutionTimer();
            UpdateExecutionStatus();

            bool shouldClose = _closeAfterExecution;
            _closeAfterExecution = false;
            if (shouldClose)
            {
                if (_window != null)
                    _window.Close();
                return;
            }

            if (_execStatusText != null)
                _execStatusText.Text += _execErrors > 0 ? "；执行结束（存在错误）" : "；执行完成";
            if (_executeButton != null)
            {
                _executeButton.Content = "再次执行";
                _executeButton.IsEnabled = true;
            }
            SetExecutionUiEnabled(true);
            UpdateStatus();
            WriteOutputs(_execQueue);
            EnsureDefaultPreset();
        }));
    }
    catch
    {
        // 窗口已关闭时忽略。
    }
}

private static void SetApplyEnabled(bool enabled)
{
    if (_applyButton != null)
        _applyButton.IsEnabled = enabled;
}

private static void SetExecutionUiEnabled(bool enabled)
{
    if (_configPanel != null)
        _configPanel.IsEnabled = enabled;
    if (_executeButton != null)
        _executeButton.IsEnabled = enabled;
    if (_loadPresetButton != null)
        _loadPresetButton.IsEnabled = enabled;
    if (_savePresetButton != null)
        _savePresetButton.IsEnabled = enabled;
    if (_realTimeCheck != null)
        _realTimeCheck.IsEnabled = enabled;
    if (_finishButton != null)
        _finishButton.IsEnabled = enabled;
    if (_execParallelCombo != null)
        _execParallelCombo.IsEnabled = enabled;
    if (_execVerifyCheck != null)
        _execVerifyCheck.IsEnabled = enabled;
    if (!enabled)
    {
        if (_applyButton != null)
            _applyButton.IsEnabled = false;
        if (_previewButton != null)
            _previewButton.IsEnabled = false;
    }
}

private static void WriteOutputs(List<Dictionary<string, object>> sourceRows)
{
    if (_data == null || DeliveryMode == 2)
        return;

    List<object> originalPaths = new List<object>();
    List<object> targetPaths = new List<object>();
    List<object> actions = new List<object>();
    for (int i = 0; i < sourceRows.Count; i++)
    {
        Dictionary<string, object> item = sourceRows[i];
        if (GetBool(item, "IsSkipped"))
            continue;
        string action = Convert.ToString(item["Action"], CultureInfo.InvariantCulture);
        if (action == null || action.Length == 0 || action == "跳过")
            continue;
        string target = Convert.ToString(item["TargetPath"], CultureInfo.InvariantCulture);
        if (target == null || target.Length == 0)
            continue;
        originalPaths.Add(Convert.ToString(item["SourcePath"], CultureInfo.InvariantCulture));
        targetPaths.Add(target);
        actions.Add(action);
    }

    if (originalPaths.Count != targetPaths.Count || originalPaths.Count != actions.Count)
    {
        MessageBox.Show("输出列表配对失败，请重试。", "批量重命名器",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
    }

    _data["origPaths"] = originalPaths;
    _data["targetPaths"] = targetPaths;
    _data["actions"] = actions;
}

private static void EnsureDefaultPreset()
{
    if (_data == null)
        return;

    Dictionary<string, Dictionary<string, object>> store = LoadPresetStore();
    if (store.Count == 0)
        store["默认"] = BuildPresetDictionary(_config);
    // 即使已有多套预设，也在完成/执行前回写一次，确保数据映射或动作变量拿到最新结构。
    SavePresetStore(store);

    Dictionary<string, string> extensionStore = LoadExtensionPresetStore();
    if (extensionStore.Count > 0)
        SaveExtensionPresetStore(extensionStore);
}

private static void Window_Closing(object sender, CancelEventArgs e)
{
    if (!_executing)
        return;

    DialogResult answer = MessageBox.Show(
        "正在执行文件操作。\n\n确定要停止并关闭窗口吗？\n已开始的文件操作会完成，尚未开始的项目不再执行。",
        "批量重命名器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
    if (answer != DialogResult.Yes)
    {
        e.Cancel = true;
        return;
    }

    e.Cancel = true;
    _execStop = true;
    _closeAfterExecution = true;
    if (_execStatusText != null)
        _execStatusText.Text = "正在停止执行，请稍候…";
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
    SetTemplateText(GetDefaultTemplateForMode(GetComboTag(_filterModeCombo) ?? "skip"));
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
    _execStop = true;
    _executing = false;
    StopExecutionTimer();
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
    _finishButton = null;
    _previewButton = null;
    _realTimeCheck = null;
    _filterExtensionsText = null;
    _filterModeCombo = null;
    _loadExtPresetButton = null;
    _saveExtPresetButton = null;
    _fallback1Combo = null;
    _fallback2Combo = null;
    _fallback3Combo = null;
    _fallback4Combo = null;
    _fallback5Combo = null;
    _fallbackCustomText = null;
    _errorBanner = null;
    _errorBannerText = null;
    _presetPopup = null;
    _presetListBox = null;
    _loadPresetButton = null;
    _savePresetButton = null;
    _configPanel = null;
    _execParallelCombo = null;
    _execVerifyCheck = null;
    _executeButton = null;
    _execProgress = null;
    _execStatusText = null;
    _rows = new ObservableCollection<Dictionary<string, object>>();
    _files = new List<Dictionary<string, object>>();
    _paths = new List<string>();
    _pathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    _hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    _config = null;
}
