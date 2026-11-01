'由于包含加解密等安全信息，本文件中的部分代码已被删除

Friend Module ModSecret

    '标注 PCL 的不同分支，仅用于替换标记
    Public Const VersionBranchMain As String = "OpenSource"
    '在开源版的注册表与常规版的注册表隔离，以防数据冲突
    Public Const RegFolder As String = "PCLDebug"
    '用于微软登录的 ClientId
    '本修改版自有的 Azure 应用（公开客户端，无 secret，Client ID 属公开信息）
    '环境变量 PCL_MS_CLIENT_ID 仍然优先，便于临时切换测试
    Public OAuthClientId As String = If(Environment.GetEnvironmentVariable("PCL_MS_CLIENT_ID"), "504721c5-1f26-4d60-afa4-ae3f4b65be64")
    'CurseForge API Key
    '客户端默认不带任何 CurseForge 凭证：API 请求走服务端代理，key 只在服务器上。
    '只有在本地调试时用环境变量 PCL_CURSEFORGE_API_KEY 直连才会用到它。
    Public CurseForgeAPIKey As String = If(Environment.GetEnvironmentVariable("PCL_CURSEFORGE_API_KEY"), "")

    ''' <summary>
    ''' CurseForge API 代理前缀（形如 host/cf），由 OtaBase 推导，
    ''' 这样换域名或换 CDN 时两处一起变。
    ''' </summary>
    ''' <summary>更新日志页地址（由服务端根据发布记录自动生成）。</summary>
    Friend Function OtaChangelogUrl() As String
        Return OtaBase.TrimEnd("/"c) & "/changelog"
    End Function

    Friend Function CurseForgeProxyHost() As String
        Dim B As String = OtaBase.TrimEnd("/"c)
        Dim Idx As Integer = B.IndexOf("://", StringComparison.Ordinal)
        If Idx >= 0 Then B = B.Substring(Idx + 3)
        If B.EndsWith("/pcl2", StringComparison.OrdinalIgnoreCase) Then
            B = B.Substring(0, B.Length - 5)
        End If
        Return B & "/cf"
    End Function
    '用于匿名数据收集的腾讯云日志服务上报 URL，形如 https://{region}.cls.tencentcs.com/track?topic_id={topic_id}
    Public Const ClsBaseUrl As String = ""

#Region "网络鉴权"

    Friend Function SecretCdnSign(UrlWithMark As String) As String
        If Not UrlWithMark.EndsWithF("{CDN}") Then Return UrlWithMark
        Return UrlWithMark.Replace("{CDN}", "").Replace(" ", "%20")
    End Function
    ''' <summary>已验签的联机模块清单（/pcl2/module.json）。Nothing 表示用编译期内置配置。</summary>
    Friend LinkModule As JObject = Nothing

    ''' <summary>
    ''' 拉取联机模块清单并验签。签名覆盖「版本 + 按架构排序的 url/sha256」，
    ''' 所以维护者只要改服务端一个文件就能升级 EasyTier，不必重新发客户端；
    ''' 而哈希受同一把 RSA 私钥保护，拦截下载也换不掉。
    ''' </summary>
    Friend Function LoadLinkModuleFromOta() As JObject
        Try
            Dim Raw As String = NetRequestByClient(OtaBase & "module.json", RequireJson:=True)
            Dim Obj As JObject = JObject.Parse(Raw)
            Dim SigText As String = If(Obj("sig")?.ToString(), "").Trim
            If SigText.Length = 0 Then
                Logger.Warn("联机模块清单缺少签名，改用内置配置")
                Return Nothing
            End If
            Dim Ver As Integer = If(Obj("version") IsNot Nothing, Obj("version").ToObject(Of Integer)(), 0)
            Dim Files As JObject = TryCast(Obj("files"), JObject)
            If Files Is Nothing Then Return Nothing
            '规范串必须与服务端逐字节一致：module + 版本 + 按架构名排序的 url/sha256
            Dim Parts As New List(Of String)
            Parts.Add("module")
            Parts.Add(Ver.ToString())
            Dim ArchNames As List(Of String) = Files.Properties().Select(Function(p) p.Name).ToList()
            ArchNames.Sort(StringComparer.Ordinal)
            For Each Arch As String In ArchNames
                Dim Entry As JObject = TryCast(Files(Arch), JObject)
                If Entry Is Nothing Then Continue For
                Parts.Add(String.Join(Chr(31).ToString(), {
                    Arch,
                    If(Entry("url")?.ToString(), ""),
                    If(Entry("sha256")?.ToString(), "").Trim().ToLower()}))
            Next
            VerifyRsaSignature(Encoding.UTF8.GetBytes(String.Join(Chr(31).ToString(), Parts)),
                               SigText, "联机模块清单")
            Logger.Info($"联机模块清单校验通过：版本 {Ver}，架构 {String.Join(", ", ArchNames)}")
            Return Obj
        Catch ex As Exception
            '404 = 服务端还没配置模块清单，这是正常状态（退内置常量即可），不必刷警告
            If ex.Message.Contains("404") Then
                Logger.Info("服务端未配置联机模块清单，使用内置版本与哈希")
            Else
                Logger.Warn(ex, "获取联机模块清单失败，改用内置配置")
            End If
            Return Nothing
        End Try
    End Function

    ''' <summary>清单里的模块版本号；没有清单返回 0。</summary>
    Friend Function LinkModuleVersion() As Integer
        Try
            If LinkModule Is Nothing Then Return 0
            If LinkModule("version") Is Nothing Then Return 0
            Return LinkModule("version").ToObject(Of Integer)()
        Catch
            Return 0
        End Try
    End Function

    ''' <summary>取某个架构的清单条目；没有则返回 Nothing。</summary>
    Friend Function LinkModuleEntry(Arch As String) As JObject
        Try
            If LinkModule Is Nothing Then Return Nothing
            Dim Files As JObject = TryCast(LinkModule("files"), JObject)
            If Files Is Nothing Then Return Nothing
            Return TryCast(Files(Arch), JObject)
        Catch
            Return Nothing
        End Try
    End Function
    ''' <summary>
    ''' 设置 Headers 的 UA、Referer。
    ''' </summary>
    Friend Sub SecretHeadersSign(Url As String, ByRef Req As HttpRequestMessage, Optional SimulateBrowserHeaders As Boolean = False)
        If Not Req.Headers.UserAgent.Any Then
            If Url.Contains("baidupcs.com") OrElse Url.Contains("baidu.com") Then
                Req.Headers.Add("User-Agent", "LogStatistic")  '#4951
            ElseIf SimulateBrowserHeaders Then
                Req.Headers.Add("User-Agent", $"PCL2/{VersionBaseName}.{CInt(BuildType)} Mozilla/5.0 AppleWebKit/537.36 Chrome/63.0.3239.132 Safari/537.36")
            Else
                Req.Headers.Add("User-Agent", $"PCL2/{VersionBaseName}.{CInt(BuildType)}")
            End If
        End If
        If Not SimulateBrowserHeaders Then Req.Headers.Add("Referer", $"http://{VersionCode}.open.pcl2.server/")
        '只有在本地通过环境变量配了 key 时才带；正常走代理时 URL 是自家域名，不需要也不该带
        If CurseForgeAPIKey <> "" AndAlso
           (Url.Contains("api.curseforge.com") OrElse Url.Contains("forgecdn.net")) Then
            Req.Headers.Add("x-api-key", CurseForgeAPIKey)
        End If
    End Sub

#End Region

#Region "主题"

    Public Color1 As New MyColor(52, 61, 74)
    Public Color2 As New MyColor(11, 91, 203)
    Public Color3 As New MyColor(19, 112, 243)
    Public Color4 As New MyColor(72, 144, 245)
    Public Color5 As New MyColor(150, 192, 249)
    Public Color6 As New MyColor(213, 230, 253)
    Public Color7 As New MyColor(222, 236, 253)
    Public Color8 As New MyColor(234, 242, 254)
    Public ColorBg0 As New MyColor(150, 192, 249)
    Public ColorBg1 As New MyColor(190, Color7)
    Public ColorGray1 As New MyColor(64, 64, 64)
    Public ColorGray2 As New MyColor(115, 115, 115)
    Public ColorGray3 As New MyColor(140, 140, 140)
    Public ColorGray4 As New MyColor(166, 166, 166)
    Public ColorGray5 As New MyColor(204, 204, 204)
    Public ColorGray6 As New MyColor(235, 235, 235)
    Public ColorGray7 As New MyColor(240, 240, 240)
    Public ColorGray8 As New MyColor(245, 245, 245)
    Public ColorSemiTransparent As New MyColor(1, Color8)

    Public ThemeNow As Integer = -1
    Public ColorHue As Integer = 210, ColorSat As Integer = 85, ColorLightAdjust As Integer = 0, ColorHueTopbarDelta As OneOf(Of Integer, Integer()) = 0
    Public ThemeDontClick As Integer = 0

    Public Sub ThemeRefresh(Optional NewTheme As Integer = -1)
        Try
            If ThemeNow = NewTheme AndAlso NewTheme >= 0 Then Return
            If NewTheme >= 0 Then ThemeNow = NewTheme

            '主题配色映射。「自定义」无限配色已移除（保留给作者），若存有旧值则回退到默认主题
            '合规：PCL 合理使用指南·重度使用第 5 条 —— 界面主要色相必须为蓝色，
            '因此无论保存过什么主题，一律回落到龙猫蓝（0），不再提供配色切换。
            If ThemeNow >= 0 Then ThemeNow = 0
            If ThemeNow >= 0 Then ThemeApplyPreset(ThemeNow)

            Color1 = New MyColor().FromHSL2(ColorHue, ColorSat * 0.2, 25 + ColorLightAdjust * 0.3)
            Color2 = New MyColor().FromHSL2(ColorHue, ColorSat, 45 + ColorLightAdjust)
            Color3 = New MyColor().FromHSL2(ColorHue, ColorSat, 55 + ColorLightAdjust)
            Color4 = New MyColor().FromHSL2(ColorHue, ColorSat, 65 + ColorLightAdjust)
            Color5 = New MyColor().FromHSL2(ColorHue, ColorSat, 80 + ColorLightAdjust * 0.4)
            Color6 = New MyColor().FromHSL2(ColorHue, ColorSat, 91 + ColorLightAdjust * 0.1)
            Color7 = New MyColor().FromHSL2(ColorHue, ColorSat, 95)
            Color8 = New MyColor().FromHSL2(ColorHue, ColorSat, 97)
            ColorBg0 = Color4 * 0.4 + Color5 * 0.4 + ColorGray4 * 0.2
            ColorBg1 = New MyColor(190, Color7)

            ColorSemiTransparent = New MyColor(1, Color8)
            Application.Current.Resources("ColorBrush1") = New SolidColorBrush(Color1)
            Application.Current.Resources("ColorBrush2") = New SolidColorBrush(Color2)
            Application.Current.Resources("ColorBrush3") = New SolidColorBrush(Color3)
            Application.Current.Resources("ColorBrush4") = New SolidColorBrush(Color4)
            Application.Current.Resources("ColorBrush5") = New SolidColorBrush(Color5)
            Application.Current.Resources("ColorBrush6") = New SolidColorBrush(Color6)
            Application.Current.Resources("ColorBrush7") = New SolidColorBrush(Color7)
            Application.Current.Resources("ColorBrush8") = New SolidColorBrush(Color8)
            Application.Current.Resources("ColorBrushBg0") = New SolidColorBrush(ColorBg0)
            Application.Current.Resources("ColorBrushBg1") = New SolidColorBrush(ColorBg1)
            Application.Current.Resources("ColorObject1") = CType(Color1, Color)
            Application.Current.Resources("ColorObject2") = CType(Color2, Color)
            Application.Current.Resources("ColorObject3") = CType(Color3, Color)
            Application.Current.Resources("ColorObject4") = CType(Color4, Color)
            Application.Current.Resources("ColorObject5") = CType(Color5, Color)
            Application.Current.Resources("ColorObject6") = CType(Color6, Color)
            Application.Current.Resources("ColorObject7") = CType(Color7, Color)
            Application.Current.Resources("ColorObject8") = CType(Color8, Color)
            Application.Current.Resources("ColorObjectBg0") = CType(ColorBg0, Color)
            Application.Current.Resources("ColorObjectBg1") = CType(ColorBg1, Color)
            ThemeRefreshMain()
        Catch ex As Exception
            Logger.Error(ex, "刷新主题颜色失败", LogBehavior.Toast)
        End Try
    End Sub

    ''' <summary>
    ''' 主题 ID 到配色的映射。开源版原实现已被移除，此处为重新实现，
    ''' 色值按主题名称近似取定，与官方版的具体数值未必完全一致。
    ''' </summary>
    Public Sub ThemeApplyPreset(Id As Integer)
        Select Case Id
            Case 0  ' 龙猫蓝
                ColorHue = 210 : ColorSat = 85 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 1  ' 甜柠青
                ColorHue = 175 : ColorSat = 62 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 2  ' 小草绿
                ColorHue = 110 : ColorSat = 58 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 3  ' 菠萝黄
                ColorHue = 45 : ColorSat = 88 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 4  ' 橡木棕
                ColorHue = 30 : ColorSat = 45 : ColorLightAdjust = -5 : ColorHueTopbarDelta = 0
            Case 5  ' 玄素黑
                ColorHue = 210 : ColorSat = 0 : ColorLightAdjust = -12 : ColorHueTopbarDelta = 0
            Case 6  ' 铁杆粉
                ColorHue = 330 : ColorSat = 62 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 7  ' 神秘紫
                ColorHue = 272 : ColorSat = 68 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 8  ' 秋仪金
                ColorHue = 40 : ColorSat = 62 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 9  ' 活跃橙
                ColorHue = 24 : ColorSat = 88 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 10 ' 跳票红
                ColorHue = 355 : ColorSat = 72 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
            Case 11 ' 极客蓝
                ColorHue = 218 : ColorSat = 92 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 6
            Case 12 ' 滑稽彩：随时间轮转色相
                ColorHue = CInt(Environment.TickCount / 4000) Mod 360 : ColorSat = 85 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 20
            Case 13 ' 欧皇彩：每次刷新随机
                ColorHue = New Random().Next(0, 360) : ColorSat = 85 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 15
            Case Else
                ColorHue = 210 : ColorSat = 85 : ColorLightAdjust = 0 : ColorHueTopbarDelta = 0
        End Select
    End Sub
    Public Sub ThemeRefreshMain()
        RunInUi(
        Sub()
            If Not FrmMain.IsLoaded Then Return
            '顶部条背景
            Dim Brush = New LinearGradientBrush With {.EndPoint = New Point(1, 0), .StartPoint = New Point(0, 0)}
            Dim Deltas = ColorHueTopbarDelta.Switch(Function(d) New Integer() {-d, 0, d}, Function(d) d)
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0, .Color = New MyColor().FromHSL2(ColorHue + Deltas(0), ColorSat, 48 + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 0.5, .Color = New MyColor().FromHSL2(ColorHue + Deltas(1), ColorSat, 54 + ColorLightAdjust)})
            Brush.GradientStops.Add(New GradientStop With {.Offset = 1, .Color = New MyColor().FromHSL2(ColorHue + Deltas(2), ColorSat, 48 + ColorLightAdjust)})
            FrmMain.PanTitle.Background = Brush
            FrmMain.PanTitle.Background.Freeze()
            '主页面背景
            If Settings.Get(Of Boolean)("UiBackgroundColorful") Then
                Brush = New LinearGradientBrush With {.EndPoint = New Point(0.1, 1), .StartPoint = New Point(0.9, 0)}
                Brush.GradientStops.Add(New GradientStop With {.Offset = -0.1, .Color = New MyColor().FromHSL2(ColorHue - 15, ColorSat * 0.8, 91)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 0.4, .Color = New MyColor().FromHSL2(ColorHue, ColorSat * 0.8, 91)})
                Brush.GradientStops.Add(New GradientStop With {.Offset = 1.1, .Color = New MyColor().FromHSL2(ColorHue + 15, ColorSat * 0.8, 91)})
                FrmMain.PanForm.Background = Brush
            Else
                FrmMain.PanForm.Background = New MyColor(245, 245, 245)
            End If
            FrmMain.PanForm.Background.Freeze()
        End Sub)
    End Sub
    Friend Sub ThemeCheckAll(EffectSetup As Boolean)
    End Sub
    Friend Function ThemeCheckOne(Id As Integer) As Boolean
        Return True
    End Function
    Friend Function ThemeUnlock(Id As Integer, Optional ShowDoubleHint As Boolean = True, Optional UnlockHint As String = Nothing) As Boolean
        Return False
    End Function

#End Region

#Region "更新"

    '自建 OTA 服务地址，末尾必须保留斜杠
    Friend Const OtaBase As String = "https://pcl.umrca.com/pcl2/"

    '更新包在临时目录中的文件名。刻意与官方版区分，避免共用一个临时目录时互相覆盖
    Friend Const UpdateFileName As String = "LatestOta.exe"

    Friend IsUpdateWaitingRestart As Boolean = False
    ''' <summary>本次更新包期望的 SHA256，启动更新程序前会用它再复核一次。</summary>
    Friend UpdateExpectedSha As String = ""

    ''' <summary>
    ''' 拉取 OTA 版本信息，同时刷新 Notice.cfg 供 IsLauncherNewest 判断。
    ''' </summary>
    Friend Function GetOtaVersion() As JObject
        Dim Raw As String = NetRequestByClientRetry(OtaBase & "version.json", RequireJson:=True)
        Dim Obj As JObject = JObject.Parse(Raw)
        '先验签，通过后才使用这份数据（防止中间人同时改写 url 与 sha256）
        VerifyVersionSignature(Obj)
        Try
            Dim Code As Integer = Obj("versionCode").ToObject(Of Integer)()
            'Notice.cfg 格式：快照版版本号|正式版版本号|公告ID
            '唯一读取方是 PageSetupSystem.IsLauncherNewest()，它只用第 2、3 个字段
            '（正式版 / 快照版版本号），公告 ID 从来没有被读过。
            '原来为了填这个字段，每次检查更新都要多拉一次 notice.json；
            '启动时叠加自动检查与手动检查，能拉到 4 次请求，纯属浪费。
            FileUtils.Write(PathTemp & "Cache\Notice.cfg", $"{Code}|{Code}|", Encoding.UTF8)
        Catch ex As Exception
            Logger.Warn(ex, "写入 Notice.cfg 失败")
        End Try
        Return Obj
    End Function

    ''' <summary>
    ''' 检查更新。有新版则询问、下载、校验 SHA256，然后重启自身完成替换。
    ''' </summary>
    ''' <summary>更新检查的并发闸门：自动检查与手动检查可能同时触发。</summary>
    Private UpdateCheckGate As New Object
    Private UpdateChecking As Boolean = False

    Friend Sub UpdateCheckByButton(Optional Silent As Boolean = False)
        '两个入口同时跑会把同一个 LatestOta.exe 边删边写，导致校验失败或弹两次窗
        SyncLock UpdateCheckGate
            If UpdateChecking Then
                Logger.Info("已有更新检查在进行中，跳过本次")
                Return
            End If
            UpdateChecking = True
        End SyncLock
        If Not Silent Then Hint("正在检查更新……")
        RunInNewThread(
            Sub()
                Try
                    Dim Obj As JObject = GetOtaVersion()
                    Dim Code As Integer = Obj("versionCode").ToObject(Of Integer)()
                    Dim Name As String = Obj("versionName").ToString()
                    Dim Changelog As String = If(Obj("changelog")?.ToString(), "").Trim()
                    Dim ForceUpdate As Boolean = Obj("force") IsNot Nothing AndAlso Obj("force").ToObject(Of Boolean)()

                    If Code <= VersionCode Then
                        Logger.Info($"检查更新：已是最新（{VersionBaseName} / {VersionCode}）")
                        If Not Silent Then RunInUiWait(Sub() Hint($"当前已是最新版本（{VersionBaseName}）"))
                        Return
                    End If

                    Logger.Info($"检查更新：发现 {Name}（{Code}），当前 {VersionBaseName}（{VersionCode}）")
                    Dim Msg As String = $"发现新版本 {Name}（{Code}）{vbCrLf}当前版本 {VersionBaseName}（{VersionCode}）"
                    If Changelog <> "" Then Msg &= vbCrLf & vbCrLf & Changelog
                    If ForceUpdate Then Msg &= vbCrLf & vbCrLf & "此版本为强制更新，必须更新后才能继续使用。"

                    Dim Result As Integer
                    If ForceUpdate Then
                        Result = RunInUiWait(Function() MyMsgBox(Msg, "发现更新", "立即更新", "退出"))
                    Else
                        Result = RunInUiWait(Function() MyMsgBox(Msg, "发现更新", "立即更新", "稍后"))
                    End If

                    If Result <> 1 Then
                        If ForceUpdate Then RunInUiWait(Sub() FormMain.EndProgramForce(ProcessReturnValues.Cancel))
                        Return
                    End If

                    '下载更新包
                    Dim Latest As String = PathTemp & UpdateFileName
                    FileUtils.Delete(Latest)
                    RunInUiWait(Sub() Hint("正在下载更新包……"))
                    '更新包约 5.5 MB，默认 25 秒超时在慢速链路上必然失败（重试 3 次也全超时）。
                    '这里放宽到 120 秒，配合 NetDownloadByClient 自带的 3 次重试。
                    NetDownloadByClient(Obj("url").ToString(), Latest, Timeout:=120000)

                    '大小校验
                    Dim ExpectSize As Long = If(Obj("size") IsNot Nothing, Obj("size").ToObject(Of Long)(), 0L)
                    Dim ActualSize As Long = FileUtils.GetInfo(Latest).Length
                    If ExpectSize > 0 AndAlso ActualSize <> ExpectSize Then
                        FileUtils.Delete(Latest)
                        Logger.Warn($"更新包大小不符：期望 {ExpectSize}，实际 {ActualSize}")
                        RunInUiWait(Sub() Hint("更新包大小不符，已取消更新"))
                        Return
                    End If

                    'SHA256 校验：exe 没有数字签名，这一步是防止被替换或下载损坏的关键
                    Dim ExpectSha As String = If(Obj("sha256")?.ToString(), "").Trim().ToLower()
                    If ExpectSha <> "" Then
                        Dim ActualSha As String = CryptographyUtils.ComputeFileHash(Latest, CryptographyUtils.HashMethod.Sha256).Trim().ToLower()
                        If ActualSha <> ExpectSha Then
                            FileUtils.Delete(Latest)
                            Logger.Warn($"更新包校验失败：期望 {ExpectSha}，实际 {ActualSha}")
                            RunInUiWait(Sub() Hint("更新包校验失败，已取消更新"))
                            Return
                        End If
                    Else
                        Logger.Warn("OTA 未提供 sha256，已跳过校验")
                    End If

                    '把这次更新的日志存下来，重启后由「已更新至」弹窗原样展示。
                    '本修改版的弹窗只认 OTA 下发的 changelog，不再拼接上游原版的 FeatureList。
                    Try
                        FileUtils.Write(PathTemp & "UpdateChangelog.txt", Changelog, Encoding.UTF8)
                    Catch ex As Exception
                        Logger.Warn(ex, "写入更新日志缓存失败")
                    End Try
                    UpdateExpectedSha = ExpectSha
                    Logger.Info($"更新包已就绪：{Latest}")
                    IsUpdateWaitingRestart = True
                    UpdateRestart(True)
                Catch ex As Exception
                    If Silent Then
                        Logger.Warn(ex, "自动检查更新失败（静默）")
                    Else
                        Logger.Error(ex, "检查更新失败")
                        RunInUiWait(Sub() Hint("检查更新失败：" & ex.Message))
                    End If
                Finally
                    SyncLock UpdateCheckGate
                        UpdateChecking = False
                    End SyncLock
                End Try
            End Sub, "检查更新")
    End Sub

    ''' <summary>
    ''' 启动后静默检查更新：只有发现新版本时才会弹窗，已是最新或网络失败都不打扰用户。
    ''' </summary>
    Friend Sub UpdateCheckAuto()
        RunInNewThread(
            Sub()
                '兜底：清理上次更新可能残留的更新程序副本。
                '延迟删除偶尔会失败（进程还没退出、文件被占用），这里收尾。
                Try
                    Dim Leftover As String = IO.Path.Combine(IO.Path.GetDirectoryName(PathExe), "PCL2_updater.exe")
                    Dim SelfPath As String = Diagnostics.Process.GetCurrentProcess().MainModule.FileName
                    If FileUtils.Exists(Leftover) AndAlso Not String.Equals(Leftover, SelfPath, StringComparison.OrdinalIgnoreCase) Then
                        FileUtils.Delete(Leftover)
                        Logger.Info($"已清理残留的更新程序：{Leftover}")
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "清理残留更新程序失败")
                End Try
                Threading.Thread.Sleep(6000) '等启动动画与各加载器稳定
                '统一走 UpdateCheckByButton：Silent 模式下无更新不打扰，
                '有更新照常弹窗。之前这里先自己 GetOtaVersion 判断一遍，
                '再调 UpdateCheckByButton 又拉一遍，导致 version.json 被请求两次。
                UpdateCheckByButton(Silent:=True)
            End Sub, "自动检查更新")
    End Sub

    ''' <summary>
    ''' 启动更新程序并（按需）结束当前进程。更新程序会等本进程退出后替换文件。
    ''' </summary>
    Public Sub UpdateRestart(TriggerRestartAndByEnd As Boolean)
        Try
            Dim Latest As String = PathTemp & UpdateFileName
            If Not FileUtils.Exists(Latest) Then
                Logger.Warn("更新包不存在，无法启动更新程序")
                Return
            End If
            IsUpdateWaitingRestart = False
            '优先把更新程序放到启动器同目录再运行：未签名 exe 从 %TEMP% 启动容易被
            '杀软/系统策略拦住，放在目标目录里成功率更高。失败则退回临时目录。
            Dim UpdaterPath As String = Latest
            Try
                Dim StagedUpdater As String = IO.Path.Combine(IO.Path.GetDirectoryName(PathExe), "PCL2_updater.exe")
                File.Copy(Latest, StagedUpdater, True)
                UpdaterPath = StagedUpdater
            Catch ex As Exception
                Logger.Warn(ex, "无法把更新程序复制到启动器目录，改为从临时目录运行")
            End Try
            Dim Args As String = $"--update {Process.GetCurrentProcess().Id} ""{PathExe}"" ""{UpdaterPath}"" True"
            '启动前再校验一次哈希：从下载完成到此刻，文件可能被本地其他进程
            '（恶意软件、被替换的同步目录）换掉。校验与执行之间的窗口越小越好。
            If UpdateExpectedSha <> "" Then
                Dim ActualSha As String =
                    CryptographyUtils.ComputeFileHash(UpdaterPath, CryptographyUtils.HashMethod.Sha256).Trim().ToLower()
                If ActualSha <> UpdateExpectedSha Then
                    Logger.Error($"更新程序启动前校验失败：期望 {UpdateExpectedSha}，实际 {ActualSha}")
                    Try
                        FileUtils.Delete(UpdaterPath)
                    Catch
                    End Try
                    RunInUiWait(Sub() Hint("更新包在下载后被改动过，已中止更新，请重新检查更新", HintType.Red))
                    Return
                End If
                Logger.Info("启动前复核更新程序哈希：一致")
            End If
            Logger.Info($"启动更新程序：{UpdaterPath} {Args}")
            StartProcess(UpdaterPath, Args)
            If TriggerRestartAndByEnd Then RunInUiWait(Sub() FormMain.EndProgramForce())
        Catch ex As Exception
            Logger.Error(ex, "启动更新程序失败")
            RunInUiWait(Sub() Hint("启动更新程序失败：" & ex.Message))
        End Try
    End Sub

    ''' <summary>
    ''' 由新版本进程在启动参数为 --update 时调用：等旧进程退出，替换 exe，再按需重启。
    ''' </summary>
    Public Sub UpdateReplace(ProcessId As Integer, OldFileName As String, NewFileName As String, TriggerRestart As Boolean)
        Try
            '1. 等待旧进程退出
            Try
                Dim OldProc As Process = Process.GetProcessById(ProcessId)
                If Not OldProc.WaitForExit(15000) Then Logger.Warn("等待旧进程退出的时间已超过 15 秒")
            Catch ex As ArgumentException
                '进程已经不存在了，直接继续
            End Try
            Thread.Sleep(400)

            '2. 替换文件：先把原文件「改名」挪开，再把新文件「改名」放进去。
            '为什么不用 File.Copy(..., overwrite:=True)：Windows 上覆盖是先 truncate
            '目标再写入，中途失败（磁盘满、杀软中断、断电）会把启动器本体截断成半个；
            '而且重试时备份到的会是已经损坏的残骸。
            '改名是原子操作、不复制内容，失败可以立刻把原文件挪回来。
            Dim StagedFile As String = OldFileName & ".new"
            Dim BackupFile As String = OldFileName & ".bak"
            Try
                File.Copy(NewFileName, StagedFile, True)
            Catch ex As Exception
                Logger.Error(ex, $"无法把更新文件拷贝到启动器目录：{ex.Message}")
                MsgBox("自动更新失败：无法把更新文件写入启动器目录。" & vbCrLf &
                       "请把下面这个文件手动复制过去覆盖原文件：" & vbCrLf & NewFileName & vbCrLf & "→" & vbCrLf & OldFileName,
                       MsgBoxStyle.Critical, "更新失败")
                Return
            End Try

            Dim Replaced As Boolean = False
            For i = 1 To 20
                Try
                    '① 原文件改名备份（不复制内容，瞬间完成）
                    If FileUtils.Exists(BackupFile) Then FileUtils.Delete(BackupFile)
                    If FileUtils.Exists(OldFileName) Then File.Move(OldFileName, BackupFile)
                    '② 新文件改名就位
                    File.Move(StagedFile, OldFileName)
                    Replaced = True
                    Exit For
                Catch ex As Exception
                    Logger.Warn($"替换启动器文件第 {i} 次失败，稍后重试：{ex.Message}")
                    '③ 回滚：目标位置空了但备份还在，就把它挪回去
                    Try
                        If Not FileUtils.Exists(OldFileName) AndAlso FileUtils.Exists(BackupFile) Then
                            File.Move(BackupFile, OldFileName)
                            Logger.Info("已回滚：把备份移回原文件名")
                        End If
                    Catch ex2 As Exception
                        Logger.Error(ex2, "回滚失败，原文件仍在 " & BackupFile)
                    End Try
                    Thread.Sleep(300)
                End Try
            Next
            Try
                FileUtils.Delete(StagedFile)
            Catch
            End Try

            If Not Replaced Then
                '最后再尝试一次恢复
                Try
                    If Not FileUtils.Exists(OldFileName) AndAlso FileUtils.Exists(BackupFile) Then
                        File.Move(BackupFile, OldFileName)
                        Logger.Info("替换失败后已恢复原文件")
                    End If
                Catch
                End Try
                Logger.Error($"替换启动器文件失败：{NewFileName} → {OldFileName}")
                Dim Extra As String = If(FileUtils.Exists(BackupFile), vbCrLf & vbCrLf & "原文件已备份到：" & vbCrLf & BackupFile, "")
                MsgBox("自动更新失败，请手动复制以下文件并覆盖原文件：" & vbCrLf &
                       NewFileName & vbCrLf & "→" & vbCrLf & OldFileName & Extra, MsgBoxStyle.Critical, "更新失败")
                Return
            End If
            Logger.Info($"已替换启动器文件：{OldFileName}")
            '成功后清理更新程序副本与备份。
            '注意：更新程序自己就是 NewFileName（副本放在启动器同目录再运行），
            '它此刻正在运行，Windows 不允许删除自己的映像，直接删一定失败，
            '会在用户桌面上留下一个 PCL2_updater.exe。改为延迟删除。
            Try
                Dim SelfPath As String = Diagnostics.Process.GetCurrentProcess().MainModule.FileName
                If NewFileName <> OldFileName Then
                    If String.Equals(IO.Path.GetFullPath(NewFileName), IO.Path.GetFullPath(SelfPath), StringComparison.OrdinalIgnoreCase) Then
                        '等本进程退出后再删
                        StartProcess("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & del /f /q ""{SelfPath}""")
                        Logger.Info($"已安排延迟删除更新程序副本：{SelfPath}")
                    Else
                        FileUtils.Delete(NewFileName)
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "清理更新程序副本失败")
            End Try
            Try
                FileUtils.Delete(BackupFile)
            Catch
            End Try

            '3. 按需重启
            If TriggerRestart Then StartProcess(OldFileName)
        Catch ex As Exception
            Logger.Error(ex, "自动更新替换失败")
        End Try
    End Sub

    ''' <summary>
    ''' 确保 PathTemp 下的更新包是最新正式版的 PCL，它会被用于整合包打包。
    ''' 如果不是，则下载一个。
    ''' </summary>
    Friend Sub DownloadLatestPCL(Optional LoaderToSyncProgress As LoaderBase = Nothing)
        Dim Latest As String = PathTemp & UpdateFileName
        Try
            Dim Obj As JObject = GetOtaVersion()
            Dim ExpectSha As String = If(Obj("sha256")?.ToString(), "").Trim().ToLower()
            If FileUtils.Exists(Latest) AndAlso ExpectSha <> "" Then
                Dim ExistSha As String = CryptographyUtils.ComputeFileHash(Latest, CryptographyUtils.HashMethod.Sha256).Trim().ToLower()
                If ExistSha = ExpectSha Then Return
            End If
            FileUtils.Delete(Latest)
            NetDownloadByLoader(Obj("url").ToString(), Latest, LoaderToSyncProgress)
        Catch ex As Exception
            Logger.Error(ex, "下载最新版 PCL 失败")
            Throw
        End Try
    End Sub

#End Region

#Region "联网配置"

    ''' <summary>
    ''' 联网获取的配置信息。
    ''' 若获取失败或仍在获取中，可能为 Nothing。
    ''' </summary>
    Public ServerConfig As JObject

    Public ServerLoader As New LoaderTask(Of Integer, Integer)("PCL 配置更新", Sub() RefreshServerConfig(), Priority:=ThreadPriority.BelowNormal) With {.ReloadTimeout = 1000 * 60 * 60}

    ''' <summary>
    ''' 当前可用的内置节点（已过期的会被排除）。
    ''' </summary>
    Friend Function LinkNodes() As List(Of JObject)
        Dim Result As New List(Of JObject)
        Dim Nodes As JToken = ServerConfig?("Link")?("Nodes")
        If Nodes Is Nothing Then Return Result
        For Each Node As JToken In Nodes
            If Node Is Nothing OrElse Node.Type <> JTokenType.[Object] Then Continue For
            Result.Add(DirectCast(Node, JObject))
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 节点是否已过期。expiresAt 为空视为永不过期。
    ''' </summary>
    Friend Function IsLinkNodeExpired(Node As JObject) As Boolean
        Dim Raw As String = If(Node?("expiresAt")?.ToString(), "").Trim
        If Raw.Length = 0 Then Return False
        Dim Parsed As Date
        If Not Date.TryParse(Raw, Parsed) Then Return False
        Return Date.Now > Parsed
    End Function

    ''' <summary>
    ''' 从 OTA 拉取联机节点列表并验签。
    ''' 成功时返回服务端下发的数组（**可能为空**，空就是空，不退回内置）；
    ''' 失败（网络/验签/格式）返回 Nothing，由调用方决定用缓存还是当作没有节点。
    ''' </summary>
    Friend Function LoadLinkNodesFromOta() As JArray
        Try
            Dim Raw As String = NetRequestByClient(OtaBase & "link.json", RequireJson:=True)
            Dim Obj As JObject = JObject.Parse(Raw)
            Dim SigText As String = If(Obj("sig")?.ToString(), "").Trim
            If SigText.Length = 0 Then
                Logger.Warn("节点列表缺少签名，已拒绝")
                Return Nothing
            End If
            Dim NodesJson As JToken = Obj("nodes")
            If NodesJson Is Nothing OrElse NodesJson.Type <> JTokenType.Array Then Return Nothing
            '注意：空数组是合法状态——管理员在后台把节点全删了就该是空的，
            '不能再偷偷用编译期内置节点顶上，否则用户会看到「后台没配却有节点」。
            '签名覆盖「名称/地址/到期时间」三项且与顺序相关：这里不能重排、不能过滤
            Dim Parts As New List(Of String)
            Parts.Add("link")
            For Each Node As JToken In NodesJson
                If Node Is Nothing OrElse Node.Type <> JTokenType.[Object] Then Continue For
                Parts.Add(String.Join(Chr(31).ToString(), {
                    If(Node("name")?.ToString(), ""),
                    If(Node("address")?.ToString(), ""),
                    If(Node("expiresAt")?.ToString(), "")}))
            Next
            VerifyRsaSignature(Encoding.UTF8.GetBytes(String.Join(Chr(31).ToString(), Parts)),
                               SigText, "节点列表")
            Logger.Info($"节点列表签名校验通过，共 {NodesJson.Count} 个")
            Dim Result As JArray = CType(NodesJson, JArray)
            SaveLinkNodesCache(Result)
            Return Result
        Catch ex As Exception
            Logger.Warn(ex, "获取联机节点列表失败，将尝试使用上次缓存")
            Return Nothing
        End Try
    End Function

    ''' <summary>「使用节点」里代表自建节点的哨兵值。</summary>
    Friend Const LinkCustomSentinel As String = "@custom"

    ''' <summary>当前是否处于「自建节点」模式。</summary>
    Friend Function LinkUseCustomNode() As Boolean
        Try
            Return LinkSelectedNode() = LinkCustomSentinel
        Catch ex As Exception
            Logger.Warn(ex, "判断自建节点模式失败")
            Return False
        End Try
    End Function

    ''' <summary>解析用户自己填的中继地址（英文/中文逗号分隔）。</summary>
    Friend Function LinkCustomPeerList() As List(Of String)
        Dim Result As New List(Of String)
        Try
            Dim Raw As String = Settings.Get(Of String)("LinkCustomPeer")
            If String.IsNullOrWhiteSpace(Raw) Then Return Result
            For Each Part As String In Raw.Split("，,".ToCharArray)
                Dim Addr As String = Part.Trim
                If Addr.Length > 0 Then Result.Add(Addr)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "读取自定义节点地址失败")
        End Try
        Return Result
    End Function

    ''' <summary>联机节点列表的离线缓存：拉取失败时用它，避免一断网就全空。</summary>
    Private Const LinkCacheRelPath As String = "Cache\LinkNodes.json"

    Private Sub SaveLinkNodesCache(Nodes As JArray)
        Try
            Dim Obj As New JObject(
                New JProperty("fetchedAt", Date.Now.ToString("yyyy-MM-dd HH:mm:ss")),
                New JProperty("nodes", Nodes.DeepClone()))
            FileUtils.Write(PathTemp & LinkCacheRelPath, Obj.ToString(Formatting.None), Encoding.UTF8)
        Catch ex As Exception
            Logger.Warn(ex, "写入联机节点缓存失败")
        End Try
    End Sub

    Private Function LoadLinkNodesCache() As JArray
        Try
            Dim Text As String = FileUtils.TryReadAsString(PathTemp & LinkCacheRelPath)
            If String.IsNullOrWhiteSpace(Text) Then Return Nothing
            Dim Obj As JObject = JObject.Parse(Text)
            Dim FetchedAt As String = If(Obj("fetchedAt")?.ToString(), "")
            Dim Arr As JToken = Obj("nodes")
            If Arr Is Nothing OrElse Arr.Type <> JTokenType.Array Then Return Nothing
            Logger.Info($"使用缓存的联机节点列表（抓取于 {FetchedAt}，共 {Arr.Count} 个）")
            Return CType(Arr, JArray)
        Catch ex As Exception
            Logger.Warn(ex, "读取联机节点缓存失败")
            Return Nothing
        End Try
    End Function
    ''' <summary>用户选定的节点地址；空字符串表示「自动（全部可用节点）」，"@custom" 表示自建。</summary>
    Friend Function LinkSelectedNode() As String
        'Settings.Get 对未注册的键会抛 KeyNotFoundException。这个函数在联机页
        'Loaded 时就会调用，一旦抛出去就是进页面直接闪退——所以这里兜住，
        '读不到就当「自动」，绝不让它把页面带崩。
        Try
            Return If(Settings.Get(Of String)("LinkSelectedNode"), "").Trim
        Catch ex As Exception
            Logger.Warn(ex, "读取已选联机节点失败，按「自动」处理")
            Return ""
        End Try
    End Function

    ''' <summary>记住用户选择的节点地址。</summary>
    Friend Sub SetLinkSelectedNode(Address As String)
        Try
            Settings.Set("LinkSelectedNode", If(Address, "").Trim)
        Catch ex As Exception
            Logger.Warn(ex, "保存已选联机节点失败")
        End Try
    End Sub
    ''' <summary>
    ''' 未过期的内置节点地址列表（若用户选定了某个节点，只返回那一个）。
    ''' </summary>
    Friend Function LinkNodePeers() As List(Of String)
        '自建节点模式：只用用户填的地址，服务端下发的节点完全不参与
        If LinkUseCustomNode() Then Return LinkCustomPeerList()

        Dim Selected As String = LinkSelectedNode()
        If Selected.Length > 0 Then
            Dim One = CollectLinkPeers(Selected)
            If One.Count > 0 Then Return One
            Logger.Warn($"选定的节点 {Selected} 已不可用，退回全部服务端节点")
        End If
        Return CollectLinkPeers(String.Empty)
    End Function

    ''' <summary>按指定地址收集可用节点；地址为空表示全部。</summary>
    Private Function CollectLinkPeers(OnlyAddress As String) As List(Of String)
        Dim Result As New List(Of String)
        For Each Node In LinkNodes()
            If IsLinkNodeExpired(Node) Then Continue For
            Dim Addr As String = If(Node?("address")?.ToString(), "").Trim
            If Addr.Length = 0 Then Continue For
            If OnlyAddress.Length > 0 AndAlso
               Not String.Equals(Addr, OnlyAddress, StringComparison.OrdinalIgnoreCase) Then Continue For
            Result.Add(Addr)
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 老版本只要填了「自定义节点」就一定用它；新版本默认是「自动（服务端节点）」，
    ''' 会让这类用户的地址静默失效、联机突然连不上。首次运行时迁移成「自建节点」，
    ''' 让他们在界面上能看见自己处于自建模式。
    ''' </summary>
    Private Sub MigrateLinkSelection()
        Try
            '只跑一次：否则用户在「自动」模式下随手填了自定义地址，下次启动会被
            '悄悄切成自建并生效——那等于「自动模式里填了也能用」，与设计不符。
            '标记置位后，想用自建必须自己在下拉框里选。
            If Settings.Get(Of Integer)("LinkNodeMigratedV1") = 1 Then Return
            Settings.Set("LinkNodeMigratedV1", 1)
            If LinkSelectedNode().Length > 0 Then Return
            If LinkCustomPeerList().Count = 0 Then Return
            SetLinkSelectedNode(LinkCustomSentinel)
            Logger.Info("检测到已填写自定义节点，已自动把「使用节点」切换为自建节点（仅迁移一次）")
        Catch ex As Exception
            Logger.Warn(ex, "迁移联机节点选择失败")
        End Try
    End Sub
    Friend Sub RefreshServerConfig()
        '联机（EasyTier）配置。
        '节点列表 API（uptime.easytier.cn）已下线，本修改版不内置任何公共中继地址，
        '而是由用户在「设置 → 联机 → 自定义节点」中填写。
        'Peers 留空时联机页会提示用户去填写，不会带着空地址强行启动 EasyTier。
        'EasyTier 为 LGPL-3.0，二进制直接从官方 GitHub Release 下载，不随本仓库分发。
        MigrateLinkSelection()
        LinkModule = LoadLinkModuleFromOta()
        '节点列表以服务端为唯一来源：服务端下发什么就用什么（空就是空）。
        '只有拉取失败时才退到上次成功的缓存，避免一断网就什么都用不了。
        Dim LinkNodeList As JArray = LoadLinkNodesFromOta()
        If LinkNodeList Is Nothing Then LinkNodeList = LoadLinkNodesCache()
        If LinkNodeList Is Nothing Then LinkNodeList = New JArray()
        ServerConfig = New JObject(
            New JProperty("Link", New JObject(
                New JProperty("EasyTierVersion", 2),
                New JProperty("Downloads", New JArray(
                    "https://github.com/EasyTier/EasyTier/releases/download/v2.6.4/easytier-windows-{arch}-v2.6.4.zip")),
                New JProperty("Argument", ""),
                New JProperty("Peers", New JArray()),
                New JProperty("MandatoryPeers", New JArray()),
                New JProperty("PeersBlackList", New JArray()),
                New JProperty("RandomPeer", 0),
                New JProperty("DiscoverPeerId", 0),
                New JProperty("DiscoverPeer", ""),
                New JProperty("Nodes", LinkNodeList))))
        Try
            Dim Raw As String = NetRequestByClient(OtaBase & "notice.json", RequireJson:=False)
            Dim Obj As JObject = JObject.Parse(Raw)
            If Obj("id") Is Nothing Then Return
            Dim Id As String = Obj("id").ToString()
            If Id = Settings.Get(Of String)("SystemLastNoticeId") Then Return
            Dim Content As String = Obj("content").ToString()
            '公告会原样弹给用户，等同官方通知，必须先验签
            Try
                VerifyNoticeSignature(Obj)
            Catch exSig As Exception
                Logger.Error(exSig, "公告签名校验失败，已丢弃这条公告")
                Return
            End Try
            Dim Title As String = Obj("title").ToString()
            Logger.Info($"收到新公告 [{Id}]：{Title}")
            'MyMsgBox 默认不阻塞（Button2 为空且 ForceWait=False 时直接返回），
            '而 MyMsgBoxTick 在主窗体或 PanMsg 尚未就绪时会直接返回、不显示弹窗。
            '如果这时就把 SystemLastNoticeId 记上，这条公告会被永久吞掉，用户再也看不到。
            '所以先确认界面能弹，弹不出来就留到下次启动再试。
            Dim UiReady As Boolean = False
            RunInUiWait(Sub() UiReady = (FrmMain IsNot Nothing AndAlso FrmMain.PanMsg IsNot Nothing))
            If Not UiReady Then
                Logger.Warn("主窗体尚未就绪，公告推迟到下次启动再显示")
                Return
            End If
            RunInUiWait(Sub() MyMsgBox(Content, "公告 · " & Title))
            Settings.Set("SystemLastNoticeId", Id)
        Catch ex As Exception
            Logger.Warn(ex, "获取启动器公告失败")
        End Try
    End Sub

#End Region

#Region "赞助等级"

    Public ReadOnly Property CurrentRank As DonationRank
        Get
            Return DonationRank.None
        End Get
    End Property

    Public Sub InputPotatoCode(IsUpdating As Boolean)
    End Sub
    Friend Sub GeneratePotatoCode()
    End Sub

    ''' <summary>
    ''' 获取设备识别码。
    ''' </summary>
    Friend Function GetIdentify() As String
        Return "0000-0000-0000-0000"
    End Function

#End Region

#Region "版本签名验证"

    '更新清单签名公钥（RSA-2048）。私钥只存在于 OTA 服务端，不在客户端。
    Private Const OtaPubModulus As String =
        "jeUuU01gW2US0UwRtnhE6ZDNfzjtFVXdT4AbUilrF4dvkK4/FwoteSd6JZuwsmA+fPQ0Xm2Ao3bdNqZsNOjyR99EaI9PO+flRwSH4zR0ZKBDhvF4GC9I4F4TjGy44k21iEdxuWBJy3bpbJKNor+AGhUPo/pzW7sXKnm/G+dr+1iKqdBb/buqKoKuXlDCrV7dcji35Upvkxog91fEUVXX30DAiQ0HcvwiueMFW54O2M9oQst+71Bess7SZ9rjklqVvCdV4w/WhrdhYzvLYjHACRd8FOddBNigIwkjbWpkFIJVYsegSs2ITYMj2FbaqOP5Ob10cO2P4nfHc6gmdbwxPw=="
    Private Const OtaPubExponent As String = "AQAB"

    ''' <summary>
    ''' 校验 version.json 的数字签名。失败直接抛异常，调用方不会再使用这份数据。
    ''' 签名内容是与服务端约定的「字段以 chr(31) 连接」的规范字符串，
    ''' 刻意不签 JSON 原文——两侧重新序列化 JSON 极易因键序/空格/转义而失配。
    ''' </summary>
    ''' <summary>
    ''' 校验 notice.json 的数字签名。失败抛异常，调用方不得展示这条公告。
    ''' 公告是直接弹给用户的「官方通知」，不签名就等于把钓鱼入口交给
    ''' 任何有能力劫持 TLS 的人。
    ''' </summary>
    Friend Sub VerifyNoticeSignature(Obj As JObject)
        Dim SigText As String = If(Obj("sig")?.ToString(), "").Trim
        If SigText.Length = 0 Then
            Throw New Exception("公告缺少数字签名，已拒绝（服务端可能未配置签名私钥）")
        End If
        Dim Canon As String = String.Join(Chr(31).ToString(), {
            "notice",
            Obj("id").ToString(),
            If(Obj("title")?.ToString(), ""),
            If(Obj("content")?.ToString(), "")})
        VerifyRsaSignature(Encoding.UTF8.GetBytes(Canon), SigText, "公告")
        Logger.Info("公告签名校验通过")
    End Sub
    Friend Sub VerifyVersionSignature(Obj As JObject)
        Dim SigText As String = If(Obj("sig")?.ToString(), "")
        If SigText.Trim() = "" Then
            Throw New Exception("更新信息缺少数字签名，已拒绝（服务端可能未配置签名私钥）")
        End If

        Dim Fields As String() = {
            Obj("versionCode").ToString(),
            Obj("versionName").ToString(),
            Obj("url").ToString(),
            Obj("sha256").ToString(),
            Obj("size").ToString(),
            If(Obj("force") IsNot Nothing AndAlso Obj("force").ToObject(Of Boolean)(), "1", "0"),
            If(Obj("changelog")?.ToString(), "")
        }
        Dim Canon As String = String.Join(Chr(31).ToString(), Fields)
        Dim Data As Byte() = Encoding.UTF8.GetBytes(Canon)
        VerifyRsaSignature(Data, SigText, "更新信息")
        Logger.Info("更新信息签名校验通过")
    End Sub

    ''' <summary>
    ''' 用内置公钥校验一段数据与签名。失败抛异常，调用方不再使用该数据。
    ''' </summary>
    Friend Sub VerifyRsaSignature(Data As Byte(), SigText As String, What As String)
        Dim Sig As Byte()
        Try
            Sig = Convert.FromBase64String(SigText.Trim())
        Catch ex As FormatException
            Throw New Exception(What & "的签名不是合法 Base64，已拒绝")
        End Try
        Dim Params As New System.Security.Cryptography.RSAParameters
        Params.Modulus = Convert.FromBase64String(OtaPubModulus)
        Params.Exponent = Convert.FromBase64String(OtaPubExponent)
        Dim Rsa As System.Security.Cryptography.RSA = System.Security.Cryptography.RSA.Create()
        Try
            Rsa.ImportParameters(Params)
            If Not Rsa.VerifyData(Data, Sig,
                                  System.Security.Cryptography.HashAlgorithmName.SHA256,
                                  System.Security.Cryptography.RSASignaturePadding.Pkcs1) Then
                Throw New Exception(What & "签名无效，已拒绝（可能被篡改或伪造）")
            End If
        Finally
            Rsa.Dispose()
        End Try
    End Sub

#End Region

#Region "授权"

    ''' <summary>
    ''' 真实设备标识。开源版的 GetIdentify 是固定常量，不能用来绑定设备，
    ''' 因此这里另取 MachineGuid 做哈希。
    ''' </summary>
    Friend Function GetDeviceId() As String
        Try
            Dim Raw As String = ""
            Try
                Using K = Microsoft.Win32.RegistryKey.OpenBaseKey(
                        Microsoft.Win32.RegistryHive.LocalMachine,
                        Microsoft.Win32.RegistryView.Registry64).OpenSubKey("SOFTWARE\Microsoft\Cryptography")
                    Raw = If(K?.GetValue("MachineGuid")?.ToString(), "")
                End Using
            Catch ex As Exception
                Logger.Warn(ex, "读取 MachineGuid 失败")
            End Try
            If Raw.Trim() = "" Then Raw = Environment.MachineName & "|" & Environment.UserName
            Return CryptographyUtils.ComputeHash(Raw, CryptographyUtils.HashMethod.Sha256).Substring(0, 16).ToUpper()
        Catch ex As Exception
            Logger.Error(ex, "生成设备标识失败")
            Return "UNKNOWN-DEVICE"
        End Try
    End Function

    ''' <summary>
    ''' 向 OTA 服务校验授权码。
    ''' 返回 1 通过、0 网络不可用、-1 被拒绝（原因写入 Message）。
    ''' </summary>
    Friend Function LicenseVerify(Key As String, ByRef Message As String) As Integer
        '每次请求用一次性随机码，服务端把它签进响应，旧的合法响应无法重放
        Dim Nonce As String = Guid.NewGuid().ToString("N")
        Dim Obj As JObject = Nothing

        '阶段一：只处理网络问题。连不上才算「不可达」，才允许走宽限期
        Try
            Dim Body As New JObject()
            Body("key") = Key.Trim()
            Body("device") = GetDeviceId()
            Body("version") = VersionCode
            Body("nonce") = Nonce
            Dim Resp As String = NetRequestByClient(OtaBase & "auth", HttpMethod.Post,
                                                    Body.ToString(Formatting.None), "application/json",
                                                    RequireJson:=True, Timeout:=15000)
            Obj = JObject.Parse(Resp)
        Catch ex As Exception
            Logger.Warn(ex, "授权服务器不可达")
            Message = "无法连接授权服务器"
            Return 0
        End Try

        '阶段二：响应内容校验。任何失败都算「明确拒绝」，绝不落入宽限期
        Try
            Dim Granted As Boolean = Obj("ok") IsNot Nothing AndAlso Obj("ok").ToObject(Of Boolean)()
            If Not Granted Then
                Message = If(Obj("message")?.ToString(), "授权码无效")
                Return -1
            End If

            '通过后必须验签：没有服务端私钥就伪造不出这一份响应
            Dim SigText As String = If(Obj("sig")?.ToString(), "")
            If SigText.Trim() = "" Then
                Logger.Warn("授权响应缺少签名，已拒绝")
                Message = "服务端未启用来电签名，请联系维护者"
                Return -1
            End If
            If If(Obj("nonce")?.ToString(), "") <> Nonce Then
                Logger.Warn("授权响应的 nonce 不匹配，已拒绝")
                Message = "授权响应校验失败（nonce）"
                Return -1
            End If
            If If(Obj("device")?.ToString(), "") <> GetDeviceId() Then
                Logger.Warn("授权响应的设备标识不匹配，已拒绝")
                Message = "授权响应校验失败（设备）"
                Return -1
            End If
            Dim Canon As String = String.Join(Chr(31).ToString(), {
                "auth",
                If(Granted, "1", "0"),
                If(Obj("expiresAt")?.ToString(), ""),
                If(Obj("serverTime")?.ToString(), ""),
                Obj("device").ToString(),
                Obj("nonce").ToString()
            })
            VerifyRsaSignature(Encoding.UTF8.GetBytes(Canon), SigText, "授权响应")

            Settings.Set("SystemLicenseLastOk", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            '记录有效期，供「设置 → 授权」页面展示（空字符串表示永久）
            Settings.Set("SystemLicenseExpires", If(Obj("expiresAt")?.ToString(), "").Trim())
            Return 1
        Catch ex As Exception
            Logger.Warn(ex, "授权响应校验失败：" & ex.Message)
            Message = "授权响应校验失败，请联系维护者"
            Return -1
        End Try
    End Function

    ''' <summary>距上次校验成功是否还在 7 天宽限期内。</summary>
    Friend Function LicenseWithinGrace() As Boolean
        Try
            Dim Raw As String = Settings.Get(Of String)("SystemLicenseLastOk")
            If Raw = "" Then Return False
            Dim Last As Long
            If Not Long.TryParse(Raw, Last) Then Return False
            Return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - Last) / 86400.0 <= 7.0
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 启动时的授权闸门。已有授权且校验通过则直接放行；
    ''' 服务器不可达时给 7 天宽限；其余情况要求重新输入。
    ''' </summary>
    Friend Function LicenseCheck() As Boolean
        Dim Key As String = ""
        Try
            Key = Settings.Get(Of String)("SystemLicenseKey").Trim()
        Catch ex As Exception
            Logger.Warn(ex, "读取本地授权码失败")
        End Try

        If Key <> "" Then
            'PCL 的 NetClient 禁止在 UI 线程发起非 127.x 的请求（ModNet:219），
            '而本函数运行在 Application.OnStartup 的 UI 线程上。
            '之前只在本机 127.0.0.1 测试，恰好命中它的 localhost 豁免，所以问题被掩盖。
            '这里把网络请求放到后台线程执行，UI 线程阻塞等待，避免误判成「服务器不可达」。
            Dim Result As Integer = 0
            Dim Msg As String = ""
            Dim Bg As New Threading.Thread(
                Sub()
                    Try
                        Result = LicenseVerify(Key, Msg)
                    Catch ex As Exception
                        Logger.Warn(ex, "授权校验线程异常")
                        Result = 0
                    End Try
                End Sub)
            Bg.IsBackground = True
            Bg.Start()
            If Not Bg.Join(20000) Then
                Logger.Warn("授权校验超时（20 秒）")
                Result = 0
            End If
            Select Case Result
                Case 1
                    Logger.Info($"授权校验通过（设备 {GetDeviceId()}）")
                    Return True
                Case -1
                    Logger.Warn($"授权被拒绝：{Msg}")
                    Settings.Set("SystemLicenseKey", "")
                    Key = ""
                Case Else
                    If LicenseWithinGrace() Then
                        Logger.Info("授权服务器不可达，处于 7 天宽限期内，继续启动")
                        Return True
                    End If
                    Logger.Warn("授权服务器不可达且已超出宽限期")
            End Select
        End If

        Return ShowLicenseDialog(Key)
    End Function

    ''' <summary>授权输入窗口。校验成功返回 True。</summary>
    Friend Function ShowLicenseDialog(Initial As String) As Boolean
        Try
            'CenterScreen 在多显示器/HiDPI 下会算偏，这里按主屏工作区手动居中
            Dim WA = SystemParameters.WorkArea
            Dim WinW As Double = 486, WinH As Double = 306
            Dim Win As New Window With {
                .Title = "PCL 授权验证",
                .Width = WinW,
                .Height = WinH,
                .ResizeMode = ResizeMode.NoResize,
                .WindowStartupLocation = WindowStartupLocation.Manual,
                .Left = WA.Left + (WA.Width - WinW) / 2,
                .Top = WA.Top + (WA.Height - WinH) / 2,
                .Background = New SolidColorBrush(Color.FromRgb(244, 247, 252))
            }
            Dim Root As New StackPanel With {.Margin = New Thickness(28, 22, 28, 18)}

            Root.Children.Add(New TextBlock With {
                .Text = "需要授权",
                .FontSize = 19,
                .FontWeight = FontWeights.SemiBold,
                .Foreground = New SolidColorBrush(Color.FromRgb(20, 30, 48))
            })
            Root.Children.Add(New TextBlock With {
                .Text = "本程序为个人修改版，需要授权码才能使用。请向维护者索取；首次校验会绑定当前设备。",
                .TextWrapping = TextWrapping.Wrap,
                .FontSize = 12.5,
                .Margin = New Thickness(0, 9, 0, 0),
                .Foreground = New SolidColorBrush(Color.FromRgb(100, 114, 138))
            })

            Dim Box As New TextBox With {
                .Text = Initial,
                .FontSize = 14,
                .FontFamily = New FontFamily("Consolas"),
                .Padding = New Thickness(10, 8, 10, 8),
                .Margin = New Thickness(0, 16, 0, 0),
                .VerticalContentAlignment = VerticalAlignment.Center,
                .CharacterCasing = CharacterCasing.Upper
            }
            Root.Children.Add(Box)

            Dim Err As New TextBlock With {
                .Text = "",
                .TextWrapping = TextWrapping.Wrap,
                .FontSize = 12.5,
                .MinHeight = 34,
                .Margin = New Thickness(0, 10, 0, 0),
                .Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
            }
            Root.Children.Add(Err)

            Dim Btns As New StackPanel With {
                .Orientation = Orientation.Horizontal,
                .HorizontalAlignment = HorizontalAlignment.Right,
                .Margin = New Thickness(0, 6, 0, 0)
            }
            Dim BtnOk As New Button With {.Content = "验证并进入", .Width = 116, .Height = 34, .IsDefault = True}
            Dim BtnExit As New Button With {.Content = "退出", .Width = 84, .Height = 34, .Margin = New Thickness(10, 0, 0, 0), .IsCancel = True}
            Btns.Children.Add(BtnOk)
            Btns.Children.Add(BtnExit)
            Root.Children.Add(Btns)
            Win.Content = Root

            AddHandler BtnOk.Click,
                Sub()
                    Dim Key As String = Box.Text.Trim()
                    If Key = "" Then
                        Err.Text = "请先输入授权码"
                        Return
                    End If
                    Err.Foreground = New SolidColorBrush(Color.FromRgb(100, 114, 138))
                    Err.Text = "正在校验……"
                    'PCL 的 NetClient 禁止在 UI 线程发起非 127.x 的请求（ModNet:219）。
                    '按钮点击事件跑在 UI 线程上，直接调用必然抛异常，表现为「无法连接授权服务器」。
                    '因此放到后台线程请求，完成后用 Dispatcher 回到 UI 线程更新界面（不冻结窗口）。
                    Logger.Info($"授权：提交校验，密钥长度 {Key.Length}，设备 {GetDeviceId()}")
                    BtnOk.IsEnabled = False
                    Dim Bg As New Threading.Thread(
                        Sub()
                            Dim Msg As String = ""
                            Dim VerifyResult As Integer = 0
                            Try
                                VerifyResult = LicenseVerify(Key, Msg)
                            Catch ex As Exception
                                Logger.Warn(ex, "授权校验线程异常")
                                VerifyResult = 0
                            End Try
                            Win.Dispatcher.BeginInvoke(New Action(
                                Sub()
                                    BtnOk.IsEnabled = True
                                    Logger.Info($"授权：校验结果 {VerifyResult}，消息 {Msg}")
                                    Select Case VerifyResult
                                        Case 1
                                            Settings.Set("SystemLicenseKey", Key)
                                            Win.DialogResult = True
                                        Case 0
                                            Err.Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
                                            Err.Text = "无法连接授权服务器，请检查网络后重试"
                                        Case Else
                                            Err.Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
                                            Err.Text = Msg
                                    End Select
                                End Sub))
                        End Sub)
                    Bg.IsBackground = True
                    Bg.Start()
                End Sub
            AddHandler BtnExit.Click,
                Sub()
                    Logger.Warn("授权：用户选择退出")
                    Win.DialogResult = False
                End Sub

            Box.Focus()
            Logger.Info($"授权：显示输入窗口（设备 {GetDeviceId()}）")
            Dim DialogResultValue As Boolean = CBool(Win.ShowDialog())
            Logger.Info($"授权：窗口已关闭，结果 {DialogResultValue}")
            Return DialogResultValue
        Catch ex As Exception
            Logger.Error(ex, "显示授权窗口失败，改用系统输入框")
            Try
                Dim Typed As String = Microsoft.VisualBasic.InputBox("请输入授权码：", "PCL 授权验证", Initial)
                If Typed Is Nothing OrElse Typed.Trim() = "" Then Return False
                Dim Msg As String = ""
                If LicenseVerify(Typed, Msg) = 1 Then
                    Settings.Set("SystemLicenseKey", Typed.Trim())
                    Return True
                End If
                MyMsgBox(Msg, "授权失败")
            Catch ex2 As Exception
                Logger.Error(ex2, "备用授权输入也失败了")
            End Try
            Return False
        End Try
    End Function

#End Region

End Module
