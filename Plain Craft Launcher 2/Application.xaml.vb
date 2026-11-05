Imports System.Reflection
Imports System.Windows.Threading
Imports Microsoft.Win32

Public Class Application

    '开始
    Private Sub Application_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
        Try
            '核心初始化
            MeloongCore.Main.Init("MITC")
            MeloongCore.Wpf.Main.Init()
            Logger.Instance = New PclLogger With {.logFolder = Paths.BaseThenName, .MinLevel = If(ModeDebug, LogLevel.Trace, LogLevel.Info)}
            '提升主线程优先级
            Thread.CurrentThread.Priority = ThreadPriority.Highest
            '执行开发版测试
            If BuildType = BuildTypes.Debug Then
                Try
                    ModDevelop.Start()
                Catch ex As Exception
                    Logger.Error(ex, "开发者模式测试出错", LogBehavior.Alert)
                End Try
            End If
            '检查 .NET Framework 版本
            Try
                Using ndpKey As RegistryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey("SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\")
                    If ndpKey?.GetValue("Release") IsNot Nothing AndAlso CInt(ndpKey.GetValue("Release")) < 528040 Then OldEnvironmentAssert()
                End Using
            Catch ex As Exception
                Logger.Warn(ex, "检查 .NET Framework 版本失败")
            End Try
            '修复 WPF 字体加载（#3467）
            Try
                Dim _unused As New FormattedText("", Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Fonts.SystemTypefaces.First, 96, New MyColor, DPI)
            Catch ex As UriFormatException
                Environment.SetEnvironmentVariable("windir", Environment.GetEnvironmentVariable("SystemRoot"), EnvironmentVariableTarget.User)
                Dim _unused As New FormattedText("", Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Fonts.SystemTypefaces.First, 96, New MyColor, DPI)
            End Try
            '带了 --elevation-attempt 却仍然不是管理员：说明 UAC 被取消或被策略阻止
            If e.Args IsNot Nothing AndAlso e.Args.Any(Function(a) a = "--elevation-attempt") AndAlso
               Not WindowsUtils.HasAdminRole() Then
                ModSecret.LinkElevationFailed = True
                Logger.Warn("提权重启后仍未获得管理员权限，本次会话不再提示提权")
            End If
            '从「联机需要管理员权限」重启过来时，自动打开联机页
            If e.Args IsNot Nothing AndAlso e.Args.Any(Function(a) a = "--link") Then
                FormMain.StartupOpenLink = True
                Logger.Info("检测到 --link，启动后自动打开联机页")
            End If
            '检查参数调用
            If e.Args.Length > 0 Then
                If e.Args(0) = "--update" Then
                    '自动更新
                    UpdateReplace(e.Args(1), e.Args(2).Trim(""""), e.Args(3).Trim(""""), e.Args(4))
                    Environment.Exit(ProcessReturnValues.TaskDone)
                ElseIf e.Args(0) = "--gpu" Then
                    '调整显卡设置
                    Try
                        SetGPUPreference(e.Args(1).Trim(""""))
                        Environment.Exit(ProcessReturnValues.TaskDone)
                    Catch ex As Exception
                        Environment.Exit(ProcessReturnValues.Fail)
                    End Try
                ElseIf e.Args(0).StartsWithF("--memory") Then
                    '内存优化
                    Dim Ram = My.Computer.Info.AvailablePhysicalMemory
                    Try
                        PageOtherTest.MemoryOptimizeInternal(False)
                    Catch ex As Exception
                        MsgBox(ex.Message, MsgBoxStyle.Critical, "内存优化失败")
                        Environment.Exit(-1)
                    End Try
                    If My.Computer.Info.AvailablePhysicalMemory < Ram Then '避免 ULong 相减出现负数
                        Environment.Exit(0)
                    Else
                        Environment.Exit((My.Computer.Info.AvailablePhysicalMemory - Ram) / 1024) '返回清理的内存量（K）
                    End If
                End If
            End If
            '初始化文件结构
            Try
                '把改名前的数据目录（启动器旁边的 PCL\）搬过来：图片、音乐、帮助、
            '自定义皮肤、ini 配置、日志都在里面。只在目标不存在时才搬，避免覆盖新数据。
            Try
                Dim OldBaseDir As String = Paths.Base & "PCL\"
                If IO.Directory.Exists(OldBaseDir) Then
                    Dim MovedCount As Integer = 0
                    For Each OldItem As String In IO.Directory.GetFileSystemEntries(OldBaseDir)
                        Dim TargetItem As String = Paths.Base & "MITC\" & IO.Path.GetFileName(OldItem)
                        If Not IO.File.Exists(TargetItem) AndAlso Not IO.Directory.Exists(TargetItem) Then
                            Try
                                If IO.Directory.Exists(OldItem) Then
                                    IO.Directory.Move(OldItem, TargetItem)
                                Else
                                    IO.File.Move(OldItem, TargetItem)
                                End If
                                MovedCount += 1
                            Catch exx As Exception
                                Logger.Warn(exx, $"迁移旧数据项失败：{IO.Path.GetFileName(OldItem)}")
                            End Try
                        End If
                    Next
                    Logger.Info($"已从 PCL\ 迁移 {MovedCount} 个数据项到 MITC\")
                End If
            Catch ex As Exception
                Logger.Warn(ex, "迁移旧数据目录失败")
            End Try
            '清理改名前的残留目录（PCL\）：能删就删，删不掉（被占用）就算了
            Try
                Dim OldAppData As String = IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCL")
                '只在配置已成功迁移到新目录后才删旧配置目录
                If IO.Directory.Exists(OldAppData) AndAlso
                   IO.File.Exists(IO.Path.Combine(Paths.AppDataThenName, "config.json")) Then
                    Try
                        IO.Directory.Delete(OldAppData, True)
                        Logger.Info($"已清理旧配置目录：{OldAppData}")
                    Catch exx As Exception
                        Logger.Warn(exx, "旧配置目录删除失败（可能被占用）")
                    End Try
                End If
                Dim OldTemp As String = IO.Path.Combine(IO.Path.GetTempPath(), "PCL")
                If IO.Directory.Exists(OldTemp) Then
                    Try
                        IO.Directory.Delete(OldTemp, True)
                        Logger.Info($"已清理旧临时目录：{OldTemp}")
                    Catch exx As Exception
                        Logger.Warn(exx, "旧临时目录删除失败（可能被占用）")
                    End Try
                End If
                Dim OldBaseDir2 As String = Paths.Base & "PCL\"
                If IO.Directory.Exists(OldBaseDir2) Then
                    Try
                        IO.Directory.Delete(OldBaseDir2, True)
                        Logger.Info($"已清理旧数据目录：{OldBaseDir2}")
                    Catch exx As Exception
                        Logger.Warn(exx, "旧数据目录删除失败（可能被占用）")
                    End Try
                End If
            Catch ex As Exception
                Logger.Warn(ex, "清理旧目录失败")
            End Try
            DirectoryUtils.Create(Paths.Base & "MITC\Pictures\")
                DirectoryUtils.Create(Paths.Base & "MITC\Musics\")
                CheckPermissionWithException(Paths.Base & "MITC\")
            Catch ex As Exception
                MsgBox($"MITC 没有对当前文件夹的权限（{Paths.Base}MITC\），请尝试：" & vbCrLf &
                  "1. 将 MITC 移动到其他文件夹" & If(Paths.Base.StartsWithF("C:", True), "，例如 C 盘和桌面以外的其他位置。", "。") & vbCrLf &
                  "2. 删除当前目录中的 MITC 文件夹，然后再试。" & vbCrLf &
                  "3. 右键 MITC 选择属性，打开 兼容性 中的 以管理员身份运行此程序。",
                MsgBoxStyle.Critical, "运行环境错误")
                Environment.[Exit](ProcessReturnValues.Cancel)
            End Try
RetryCacheCheck:
            Try
                DirectoryUtils.Create(PathTemp)
                CheckPermissionWithException(PathTemp)
            Catch ex As Exception
                If PathTemp = Path.GetTempPath() & "MITC\" Then
                    MyMsgBox("MITC 无法访问缓存文件夹，可能导致程序出错或无法正常使用！" & vbCrLf & vbCrLf & "错误原因：" & ex.GetDisplay(True), "缓存文件夹不可用")
                Else
                    MyMsgBox("手动设置的缓存文件夹不可用，MITC 将使用默认缓存文件夹。" & vbCrLf & vbCrLf & "错误原因：" & ex.GetDisplay(True), "缓存文件夹不可用")
                    Settings.Set("SystemSystemCache", "")
                    PathTemp = Path.GetTempPath() & "MITC\"
                    GoTo RetryCacheCheck
                End If
            End Try
            DirectoryUtils.Create(PathTemp & "Cache\")
            DirectoryUtils.Create(Paths.AppDataThenName)
        '从旧名（PCL）迁移配置：只搬配置文件，联机模块让它按新名字重新下载，
        '避免把被 WinDivert 锁住的旧目录整个复制过来。
        Try
            Dim OldDir As String = IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCL")
            Dim NewCfg As String = IO.Path.Combine(Paths.AppDataThenName, "config.json")
            If IO.Directory.Exists(OldDir) AndAlso Not IO.File.Exists(NewCfg) Then
                Dim Moved As Integer = 0
                For Each OldFile As String In IO.Directory.GetFiles(OldDir)
                    Try
                        IO.File.Copy(OldFile, IO.Path.Combine(
                            Paths.AppDataThenName, IO.Path.GetFileName(OldFile)), True)
                        Moved += 1
                    Catch exx As Exception
                        Logger.Warn(exx, $"迁移旧配置失败：{IO.Path.GetFileName(OldFile)}")
                    End Try
                Next
                Logger.Info($"已从旧目录 {OldDir} 迁移 {Moved} 个配置文件")
            End If
        Catch ex As Exception
            Logger.Warn(ex, "迁移旧配置目录失败")
        End Try
            '要求单例
            WaitingMutex(e)
            '设置 ToolTipService 默认值
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(GetType(DependencyObject), New FrameworkPropertyMetadata(100))
            '设置网络配置默认值
            ServicePointManager.Expect100Continue = False
            ServicePointManager.DefaultConnectionLimit = 10000
            ServicePointManager.UseNagleAlgorithm = False
            ServicePointManager.EnableDnsRoundRobin = True
            ServicePointManager.ReusePort = True
            If Environment.OSVersion.Version < New Version(6, 2) Then 'Windows 7 回退（#8519）
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Ssl3 Or SecurityProtocolType.Tls Or SecurityProtocolType.Tls11 Or SecurityProtocolType.Tls12
            End If
            '基础信息
            Logger.Info($"程序版本：{VersionDisplay} ({VersionCode}{If(CommitHash = "", "", $"，#{CommitHash}")})")
            If BuildType = BuildTypes.Snapshot Then
                Logger.Info($"识别码：{Identify}{If(ThemeCheckOne(9), "，已解锁反馈主题", "，未解锁反馈主题")}")
            Else
                Logger.Info($"识别码：{Identify}")
            End If
            Logger.Info($"程序路径：{PathExe}")
            Logger.Info($"系统编码：{Encoding.Default.HeaderName} ({Encoding.Default.CodePage}, GBK={IsGBKEncoding})")
            Logger.Info($"管理员权限：{WindowsUtils.HasAdminRole()}")
            '检测异常环境
            If Paths.Base.Contains(Path.GetTempPath()) OrElse Paths.Base.Contains("AppData\Local\Temp\") Then
                MyMsgBox("请将 MITC 从压缩包中解压后再使用！" & vbCrLf & "如果不会解压，可以在网上寻找教程。", "需要解压！", "我知道了", IsWarn:=True, ForceWait:=True)
                FormMain.EndProgramForce(ProcessReturnValues.Cancel)
            End If
            If Not Environment.Is64BitOperatingSystem Then
                MyMsgBox("MITC 和新版 Minecraft 均不再支持 32 位系统，请重装为 64 位系统后再进行游戏！", "环境警告", "我知道了", IsWarn:=True, ForceWait:=True)
                FormMain.EndProgramForce(ProcessReturnValues.Cancel)
            End If
            '授权校验（必须早于启动动画：未授权就不播动画）
            If Not LicenseCheck() Then
                FormMain.EndProgramForce(ProcessReturnValues.Cancel)
                Return
            End If

            '启动动画（图标 SplashScreen）已按需求移除：启动时直接进入主界面

            '授权等待不应计入加载耗时
            ApplicationStartTick = GetTimeMs()
            '计时
            Logger.Info($"第一阶段加载用时：{GetTimeMs() - ApplicationStartTick} ms")
            ApplicationStartTick = GetTimeMs()
            AniControlEnabled += 1
        Catch ex As Exception
            Dim FilePath As String = Nothing
            Try
                FilePath = PathExe
            Catch
            End Try
            MsgBox(ex.GetDisplay(True) & vbCrLf & "MITC 所在路径：" & If(String.IsNullOrEmpty(FilePath), "获取失败", FilePath), MsgBoxStyle.Critical, "MITC 初始化错误")
            FormMain.EndProgramForce(ProcessReturnValues.Exception)
        End Try
    End Sub
    ''' <summary>
    ''' 要求程序以单例运行。
    ''' 如果已在运行，则将其窗口拖出来并播放提示音，然后结束当前进程。
    ''' </summary>
    Private Shared Sub WaitingMutex(e As StartupEventArgs)
        If BuildType = BuildTypes.Debug Then Return
        Try
            Dim MutexCreatedNew As Boolean
            PclMutex = New Mutex(True, "MITC_SingletonMutex", MutexCreatedNew)
            If MutexCreatedNew Then Return
            Logger.Warn("已有一个 MITC 实例正在运行")
            '等待已有的 PCL 退出
            If e.Args.Length > 0 AndAlso e.Args(0) = "--wait" Then
                Try
                    '旧实例正在退出（例如提权重启），最多等 30 秒，避免两个实例同时跑
                If PclMutex.WaitOne(30000) Then
                    Logger.Info("已等待旧实例退出")
                    Return
                End If
                Logger.Warn("等待旧实例超时（30 秒），仍继续启动")
                Catch Ignored As AbandonedMutexException
                    Return '已有实例异常退出，继续启动
                End Try
            End If
            '将已有的 PCL 窗口拖出来
            '先按新标题找；再兜底找改名前的标题，方便从旧版本平滑升级
            Dim WindowHwnd As IntPtr = FindWindow(Nothing, "MITC　")
            If WindowHwnd = IntPtr.Zero Then WindowHwnd = FindWindow(Nothing, "MITC")
            If WindowHwnd = IntPtr.Zero Then WindowHwnd = FindWindow(Nothing, "Plain Craft Launcher　")
            If WindowHwnd = IntPtr.Zero Then WindowHwnd = FindWindow(Nothing, "Plain Craft Launcher 2　")
            If WindowHwnd <> IntPtr.Zero Then ShowWindowToTop(WindowHwnd)
            '播放提示音并退出
            Beep()
            Environment.[Exit](ProcessReturnValues.Cancel)
        Catch ex As Exception
            Logger.Warn(ex, "单例检查失败")
        End Try
    End Sub
    Private Shared PclMutex As Mutex

    '结束
    Private Sub Application_SessionEnding(sender As Object, e As SessionEndingCancelEventArgs) Handles Me.SessionEnding
        FrmMain?.EndProgram(False)
    End Sub

    '异常
    Private Sub Application_DispatcherUnhandledException(sender As Object, e As DispatcherUnhandledExceptionEventArgs) Handles Me.DispatcherUnhandledException
        On Error Resume Next
        '触发页面的 Dispatcher
        If FrmMain?.PageLeft IsNot Nothing AndAlso TypeOf FrmMain.PageLeft Is IDispatcherUnhandledException Then
            CType(FrmMain.PageLeft, IDispatcherUnhandledException).DispatcherUnhandledException(sender, e)
            If e.Handled Then Return
        End If
        If FrmMain?.PageRight IsNot Nothing AndAlso TypeOf FrmMain.PageRight Is IDispatcherUnhandledException Then
            CType(FrmMain.PageRight, IDispatcherUnhandledException).DispatcherUnhandledException(sender, e)
            If e.Handled Then Return
        End If
        '正常处理
        e.Handled = True
        If IsProgramEnding Then Return
        FeedbackInfo()
        Dim Detail As String = e.Exception.GetDisplay(True)
        If Detail.Contains("System.Windows.Threading.Dispatcher.Invoke") OrElse Detail.Contains("MS.Internal.AppModel.ITaskbarList.HrInit") OrElse Detail.Contains("未能加载文件或程序集") OrElse
           Detail.Contains(".NET Framework") Then ' “自动错误判断” 的结果分析
            OldEnvironmentAssert(e.Exception)
        Else
            Logger.Error(e.Exception, "程序出现未知错误", LogBehavior.AlertThenCrash)
        End If
    End Sub
    Private Shared Sub OldEnvironmentAssert(Optional ex As Exception = Nothing)
        OpenWebsite("https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/thank-you/net48-offline-installer")
        If ex Is Nothing Then
            Logger.Error($"你的 .NET Framework 版本过低或损坏，请下载并重新安装 .NET Framework 4.8！{vbCrLf}若安装失败，可以先卸载高版本的 .NET Framework 然后再试。", LogBehavior.AlertThenCrash)
        Else
            Logger.Error(ex, $"你的 .NET Framework 版本过低或损坏，请下载并重新安装 .NET Framework 4.8！{vbCrLf}若安装失败，可以先卸载高版本的 .NET Framework 然后再试。", LogBehavior.AlertThenCrash)
        End If
    End Sub

    '动态 DLL 加载
    Private Sub New() '这里必须尽早调用，且不能使用任何库，否则加载 MeloongCore 就会导致崩溃
        Static Prefixes As String() = {"NAudio", "Newtonsoft.Json", "Ookii.Dialogs.Wpf", "Imazen.WebP", "CacheCow.Common", "CacheCow.Client.FileStore", "CacheCow.Client", "ThrottleDebounce", "System.Net.Http.Formatting", "PCLCS", "MeloongCore.Wpf", "MeloongCore"}
        Static LoadedAssemblies As New ConcurrentDictionary(Of String, Lazy(Of Assembly))(StringComparer.Ordinal) '缓存
        AddHandler AppDomain.CurrentDomain.AssemblyResolve,
        Function(sender As Object, Args As ResolveEventArgs) As Assembly
            Dim Prefix As String = Prefixes.FirstOrDefault(Function(p) Args.Name.StartsWith(p, StringComparison.Ordinal))
            If Prefix Is Nothing Then Return Nothing
            Dim LazyAssembly = LoadedAssemblies.GetOrAdd(Prefix, Function(p) New Lazy(Of Assembly)(
            Function()
                Dim LoadedAssembly = Assembly.Load(DirectCast(My.Resources.ResourceManager.GetObject(p), Byte()))
                If p = "Imazen.WebP" Then ExtractLibwebp() 'WebP 特判
                Return LoadedAssembly
            End Function, LazyThreadSafetyMode.ExecutionAndPublication))
            Return LazyAssembly.Value
        End Function
    End Sub
    Private Shared Sub ExtractLibwebp() '这个方法会调用 ModBase，进而调用 MeloongCore，所以不能放在 New 里
        SetDllDirectory(PathPure.TrimEnd("\"c))
        Try
            ExtractResources(PathPure & "libwebp.dll", "libwebp64")
        Catch ex As Exception
            Logger.Warn(ex, "写入 libwebp.dll 失败") '防止同时加载多个图片时，同时写入文件导致文件占用，进而导致崩溃
        End Try
    End Sub
    Private Declare Function SetDllDirectory Lib "kernel32" Alias "SetDllDirectoryA" (lpPathName As String) As Boolean

    '切换窗口

    '控件模板事件
    Private Sub MyIconButton_Click(sender As Object, e As EventArgs)
        Select Case Settings.Get(Of McLoginType)("LoginType")
            Case McLoginType.Ms
                '微软
                Dim MsJson As JObject = Settings.Get(Of String)("LoginMsJson").DeserializeJson()
                MsJson.Remove(sender.Tag)
                Settings.Set("LoginMsJson", MsJson.ToString(Newtonsoft.Json.Formatting.None))
                If FrmLoginMs.ComboAccounts.SelectedItem Is sender.Parent Then FrmLoginMs.ComboAccounts.SelectedIndex = 0
                FrmLoginMs.ComboAccounts.Items.Remove(sender.Parent)
            Case McLoginType.Legacy
                '离线
                Dim Names As New List(Of String)
                Names.AddRange(Settings.Get(Of String)("LoginLegacyName").ToString.Split("¨"))
                Names.Remove(sender.Tag)
                Settings.Set("LoginLegacyName", Names.Join("¨"c))
                FrmLoginLegacy.ComboName.ItemsSource = Names
                FrmLoginLegacy.ComboName.Text = If(Names.Any, Names(0), "")
            Case Else
                '第三方
                Dim Token As String = Settings.Get(Of McLoginType)("LoginType").ToString()
                Dim Dict As New Dictionary(Of String, String)
                Dim Names As New List(Of String)
                Dim Passs As New List(Of String)
                If Not Settings.Get(Of String)("Login" & Token & "Email") = "" Then Names.AddRange(Settings.Get(Of String)("Login" & Token & "Email").Split("¨"))
                If Not Settings.Get(Of String)("Login" & Token & "Pass") = "" Then Passs.AddRange(Settings.Get(Of String)("Login" & Token & "Pass").Split("¨"))
                For i = 0 To Names.Count - 1
                    Dict.Add(Names(i), Passs(i))
                Next
                Dict.Remove(sender.Tag)
                Settings.Set("Login" & Token & "Email", Dict.Keys.Join("¨"c))
                Settings.Set("Login" & Token & "Pass", Dict.Values.Join("¨"c))
                Select Case Token
                    Case "Nide"
                        FrmLoginNide.ComboName.ItemsSource = Dict.Keys
                        FrmLoginNide.ComboName.Text = If(Dict.Keys.Any, Dict.Keys(0), "")
                        FrmLoginNide.TextPass.Password = If(Dict.Values.Any, Dict.Values(0), "")
                    Case "Auth"
                        FrmLoginAuth.ComboName.ItemsSource = Dict.Keys
                        FrmLoginAuth.ComboName.Text = If(Dict.Keys.Any, Dict.Keys(0), "")
                        FrmLoginAuth.TextPass.Password = If(Dict.Values.Any, Dict.Values(0), "")
                End Select
        End Select
    End Sub

    Public Shared ShowingTooltips As New List(Of Border)
    Private Sub TooltipLoaded(sender As Border, e As EventArgs)
        ShowingTooltips.Add(sender)
    End Sub
    Private Sub TooltipUnloaded(sender As Border, e As RoutedEventArgs)
        ShowingTooltips.Remove(sender)
    End Sub

End Class
