Imports System.ComponentModel
Imports System.Windows.Interop

Public Class FormMain

#Region "基础"

    '更新日志
    '更新日志（本修改版）
    '只展示 OTA 下发的 changelog（version.json 里已签名），不再拼接上游原版的
    'FeatureList —— 那些是原版 PCL 的更新内容，跟我们这个修改版没有关系，
    '混在一起会让用户以为我们改过那些东西。
    Private Sub ShowUpdateLog(LastVersion As Integer)
        Dim OtaChangelog As String = ""
        Try
            Dim CachePath As String = PathTemp & "UpdateChangelog.txt"
            If IO.File.Exists(CachePath) Then
                OtaChangelog = IO.File.ReadAllText(CachePath).Trim
                IO.File.Delete(CachePath)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取更新日志缓存失败")
        End Try

        Dim ContentList As New List(Of String)
        For Each Line As String In OtaChangelog.Split({vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
            Dim Trimmed As String = Line.Trim
            If Trimmed.Length > 0 Then ContentList.Add(Trimmed)
        Next
        If ContentList.Count = 0 Then
            '从原版或很老的版本升上来时没有 OTA 日志：不要编内容，直接引导看日志页
            ContentList.Add($"构建号 {VersionCode}（{VersionBaseName}）")
            ContentList.Add("本次更新没有附带更新说明")
            ContentList.Add("点击「完整更新日志」可以查看历史版本")
        End If

        Dim Content As String = "· " & ContentList.Join(vbCrLf & "· ")
        RunInNewThread(
        Sub()
            If MyMsgBox(Content, "PCL MITC 已更新至" & VersionDisplay, "确定", "完整更新日志") = 2 Then
                OpenWebsite(OtaChangelogUrl())
            End If
        End Sub, "UpdateLog Output")
    End Sub

    '窗口加载
    Private IsWindowLoadFinished As Boolean = False
    Public Sub New()
        ApplicationStartTick = GetTimeMs()
        '窗体参数初始化
        FrmMain = Me
        FrmLaunchLeft = New PageLaunchLeft
        FrmLaunchRight = New PageLaunchRight
        '版本号改变
        Dim LastVersion As Integer = Settings.Get(Of Integer)("SystemLastVersionReg")
        If LastVersion < VersionCode Then
            '触发升级
            UpgradeSub(LastVersion)
        ElseIf LastVersion > VersionCode Then
            '触发降级
            DowngradeSub(LastVersion)
        End If
        '版本隔离设置迁移
        If Not Settings.HasSaved("LaunchArgumentIndieV2") Then
            If Settings.HasSaved("LaunchArgumentIndie") Then
                Logger.Info("从老 PCL MITC 迁移版本隔离")
                Settings.Set("LaunchArgumentIndieV2", Settings.Get(Of Integer)("LaunchArgumentIndie"))
            ElseIf HasIniKey("Setup", "LaunchVersionSelect") Then
                Logger.Info("从老 PCL MITC 升级，但此前未调整版本隔离，使用老的版本隔离默认值")
                Settings.Set("LaunchArgumentIndieV2", Settings.GetDefault("LaunchArgumentIndie"))
            Else
                Logger.Info("全新的 PCL MITC，使用新的版本隔离默认值")
                Settings.Set("LaunchArgumentIndieV2", Settings.GetDefault("LaunchArgumentIndieV2"))
            End If
        End If
        '刷新主题
        ThemeCheckAll(False)
        ThemeRefresh(Settings.Get(Of Integer)("UiLauncherTheme"))
        '注册拖拽事件（不能直接加 Handles，否则没用；#6340）
        [AddHandler](DragDrop.DragEnterEvent, New DragEventHandler(AddressOf HandleDrag), handledEventsToo:=True)
        [AddHandler](DragDrop.DragOverEvent, New DragEventHandler(AddressOf HandleDrag), handledEventsToo:=True)
        '加载 UI
        InitializeComponent()
        Opacity = 0
        '开启管理员权限下的文件拖拽
        If WindowsUtils.HasAdminRole() Then
            Static Helper As New DragHelper
            AddHandler SourceInitialized,
            Sub()
                Dim WpfHelper As New WindowInteropHelper(Me)
                Helper.HwndIntPtrSource = HwndSource.FromHwnd(WpfHelper.Handle)
                Helper.AddHook()
            End Sub
            AddHandler Closing, Sub() Helper.RemoveDragHook()
            AddHandler Helper.DragDrop, Sub() FileDrag(Helper.DropFilePaths)
        End If
        '切换到首页
        If Not IsNothing(FrmLaunchLeft.Parent) Then FrmLaunchLeft.SetValue(ContentPresenter.ContentProperty, Nothing)
        If Not IsNothing(FrmLaunchRight.Parent) Then FrmLaunchRight.SetValue(ContentPresenter.ContentProperty, Nothing)
        PanMainLeft.Child = FrmLaunchLeft
        PageLeft = FrmLaunchLeft
        PanMainRight.Child = FrmLaunchRight
        PageRight = FrmLaunchRight
        FrmLaunchRight.PageState = MyPageRight.PageStates.ContentStay
        '模式提醒
        If BuildType = BuildTypes.Debug Then Hint("[开发者模式] PCL MITC 正以开发者模式运行，这可能会造成严重的性能下降，请务必立即向开发者反馈此问题！", HintType.Red)
        If ModeDebug Then Hint("[调试模式] PCL MITC 正以调试模式运行，这可能会导致性能下降，若无必要请不要开启！")
        '尽早执行的加载池
        McFolderListLoader.Start(0) '为了让下载已存在文件检测可以正常运行，必须跑一次；为了让启动按钮尽快可用，需要尽早执行；为了与 PageLaunchLeft 联动，需要为 0 而不是 GetUuid

        Logger.Info($"第二阶段加载用时：{GetTimeMs() - ApplicationStartTick} ms")
    End Sub
    Private Sub FormMain_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ApplicationStartTick = GetTimeMs()
        Handle = New WindowInteropHelper(Me).Handle
        '读取设置
        UpdateBackgroundAndTitleBar()
        PageSetupUI.HiddenRefresh()
        PageSetupUI.BackgroundRefresh(False, True)
        MusicRefreshPlay(False, True)
        '扩展按钮
        BtnExtraDownload.ShowCheck = AddressOf BtnExtraDownload_ShowCheck
        BtnExtraBack.ShowCheck = AddressOf BtnExtraBack_ShowCheck
        BtnExtraApril.ShowCheck = AddressOf BtnExtraApril_ShowCheck
        BtnExtraShutdown.ShowCheck = AddressOf BtnExtraShutdown_ShowCheck
        BtnExtraApril.ShowRefresh()
        '初始化尺寸改变
        Dim Resizer As New MyResizer(Me)
        Resizer.addResizerDown(ResizerB)
        Resizer.addResizerLeft(ResizerL)
        Resizer.addResizerLeftDown(ResizerLB)
        Resizer.addResizerLeftUp(ResizerLT)
        Resizer.addResizerRight(ResizerR)
        Resizer.addResizerRightDown(ResizerRB)
        Resizer.addResizerRightUp(ResizerRT)
        Resizer.addResizerUp(ResizerT)
        'PLC 彩蛋
        If RandomInteger(1, 1000) = 233 Then
            ShapeTitleLogo.Data = New GeometryConverter().ConvertFromString("M6,30 V2 H15 L23,17 L31,2 H40 V30 H32 V12 L24,26 L16,12 V30 Z")
        End If
        '加载窗口
        ThemeRefreshMain()
        Try
            Height = Settings.Get(Of Integer)("WindowHeight")
            Width = Settings.Get(Of Integer)("WindowWidth")
        Catch ex As Exception '修复 #2019
            Logger.Error(ex, "读取窗口默认大小失败", LogBehavior.Toast)
            Height = MinHeight + 100
            Width = MinWidth + 100
        End Try
        'MinHeight = 50
        'MinWidth = 50
        Topmost = False
        If FrmStart IsNot Nothing Then FrmStart.Close(New TimeSpan(0, 0, 0, 0, 400 / AniSpeed))
        '更改窗口
        Top = (GetWPFSize(My.Computer.Screen.WorkingArea.Height) - Height) / 2
        Left = (GetWPFSize(My.Computer.Screen.WorkingArea.Width) - Width) / 2
        IsSizeSaveable = True
        ShowWindowToTop()
        Dim HwndSource As Interop.HwndSource = PresentationSource.FromVisual(Me)
        HwndSource.AddHook(New Interop.HwndSourceHook(AddressOf WndProc))
        AniStart({
            AaCode(Sub() AniControlEnabled -= 1, 50),
            AaOpacity(Me, Settings.Get(Of Integer)("UiLauncherTransparent") / 1000 + 0.4, 250, 100),
            AaDouble(Sub(i) TransformPos.Y += i, -TransformPos.Y, 600, 100, New AniEaseOutBack(AniEasePower.Weak)),
            AaDouble(Sub(i) TransformRotate.Angle += i, -TransformRotate.Angle, 500, 100, New AniEaseOutBack(AniEasePower.Weak)),
            AaCode(
            Sub()
                PanBack.RenderTransform = Nothing
                IsWindowLoadFinished = True
                Logger.Info($"DPI：{DPI}，系统版本：{Environment.OSVersion.VersionString}，PCL MITC 位置：{PathExe}")
            End Sub, , True)
        }, "Form Show")
        'Timer 启动
        AniStart()
        TimerMainStart()
        '加载池
        RunInNewThread(
        Sub()
            'EULA 提示
            Const EulaVersion As Integer = 2
            If Settings.Get(Of Integer)("SystemEulaVersion") < EulaVersion Then
                Select Case MyMsgBox(
                    If(Settings.Get(Of Integer)("SystemEulaVersion") = 0,
                        "在使用 PCL MITC 前，请先阅读用户协议与免责声明。",
                        $"PCL MITC 的用户协议与免责声明已更新。{vbCrLf}请阅读更新后的用户协议与免责声明。"),
                        "协议授权", "同意", "拒绝", "查看用户协议与免责声明",
                        Button3Action:=Sub() OpenWebsite("https://shimo.im/docs/rGrd8pY8xWkt6ryW"))
                    Case 1
                        Settings.Set("SystemEulaVersion", EulaVersion)
                    Case 2
                        EndProgram(False)
                End Select
            End If
            '启动加载器池
            Try
                JavaInit() '延后到同意协议后再执行，避免在初次启动时进行进程操作
                Thread.Sleep(100)
                DlClientListMojangLoader.Start(1) 'PCL 会同时根据这里的加载结果决定是否使用官方源进行下载
                RunCountSub()
                ServerLoader.Start()
                RunInNewThread(AddressOf TryClearTaskTemp, "TryClearTaskTemp", ThreadPriority.BelowNormal)
            Catch ex As Exception
                Logger.Error(ex, "初始化加载池运行失败")
            End Try
            '清理自动更新文件
            Try
                FileUtils.Delete(Paths.Base & "PCL\Plain Craft Launcher 2.exe")
            Catch ex As Exception
                Logger.Warn(ex, "清理自动更新文件失败")
            End Try
            '上报
            Telemetry("启动")
            '使用说明与免责协议（首次启动需要同意）
            If Not Settings.Get(Of Boolean)("SystemDisclaimerAccepted") Then
                Dim DisclaimerResult As Integer = MyMsgBox(
                    "【版权声明】" & vbCrLf &
                    "Plain Craft Launcher（PCL）版权归原作者 龙腾猫跃 所有。" & vbCrLf &
                    "本程序基于其开源源码修改编译，非官方发布，未获原作者授权或认可。" & vbCrLf & vbCrLf &
                    "【免责声明】" & vbCrLf &
                    "1. 本程序与 龙腾猫跃、Mojang、Microsoft 均无关联。" & vbCrLf &
                    "2. 仅供个人学习与自用，请勿分发、售卖或用于商业用途。" & vbCrLf &
                    "3. 修改版可能不稳定或功能缺失，使用风险由使用者自行承担。" & vbCrLf &
                    "4. 不对游戏存档、账号数据、设备安全作任何担保。" & vbCrLf &
                    "5. PCL 本身免费且无广告，若你认可它，请通过官方渠道支持原作者。" & vbCrLf & vbCrLf &
                    "点击「同意并继续」表示你已阅读并接受以上内容。",
                    "版权声明与免责声明", "同意并继续", "退出")
                If DisclaimerResult = 2 Then
                    '仅当明确选择「退出」时才结束程序，避免异常环境下误退出
                    EndProgramForce(ProcessReturnValues.Cancel)
                    Return
                End If
                Settings.Set("SystemDisclaimerAccepted", True)
            End If
        End Sub, "初始化", ThreadPriority.Lowest)

        Logger.Info($"第三阶段加载用时：{GetTimeMs() - ApplicationStartTick} ms")
        '提权没能成功：明确告诉用户原因，别让他反复点
        If ModSecret.LinkElevationFailed Then
            Try
                RunInUiWait(Sub() Hint(
                    "没能获得管理员权限（UAC 被取消或被系统策略阻止），联机功能本次不可用；其它功能不受影响。",
                    HintType.Red))
            Catch ex As Exception
                Logger.Warn(ex, "提示提权失败时异常")
            End Try
        End If
        '提权重启过来时直接进联机页
        If StartupOpenLink Then
            StartupOpenLink = False
            Try
                PageChange(PageType.Link)
                Logger.Info("已自动切换到联机页")
            Catch ex As Exception
                Logger.Warn(ex, "自动切换联机页失败")
            End Try
        End If
        '启动后静默检查一次更新，有新版本才弹窗
        UpdateCheckAuto()
    End Sub
    '根据打开次数触发的事件
    Private Sub RunCountSub()
        Settings.Set("SystemCount", Settings.Get(Of Integer)("SystemCount") + 1)
        If BuildType = BuildTypes.Snapshot Then
            Select Case Settings.Get(Of Integer)("SystemCount")
                Case 1
                    MyMsgBox("欢迎使用 PCL MITC 快照版！" & vbCrLf &
                             "快照版包含尚未在正式版发布的测试性功能，仅用于赞助者本人尝鲜。所以请不要发给其他人或者用于制作整合包哦！" & vbCrLf &
                             "如果你并非通过赞助或赞助者本人邀请进群获得的本程序，那么可能是有人在违规传播，记得提醒他一下啦。", "快照版使用说明")
            End Select
            If Settings.Get(Of Integer)("SystemCount") >= 99 Then
                If ThemeUnlock(6, False) Then
                    MyMsgBox("你已经使用了 99 次 PCL MITC 啦，感谢你长期以来的支持！" & vbCrLf &
                             "隐藏主题 铁杆粉 已解锁！", "提示")
                End If
            End If
        End If
    End Sub
    '升级与降级事件
    Private Sub UpgradeSub(LastVersionCode As Integer)
        Logger.Info($"版本号从 {LastVersionCode} 升高到 {VersionCode}")
        Settings.Set("SystemLastVersionReg", VersionCode)
        '检查有记录的最高版本号
        Dim HighestVersionCode As Integer
        Const SettingKey = If(BuildType = BuildTypes.Snapshot, "SystemHighestAlphaVersionReg", "SystemHighestBetaVersionReg")
        HighestVersionCode = Settings.Get(Of Integer)(SettingKey)
        If HighestVersionCode < VersionCode Then
            Settings.Set(SettingKey, VersionCode)
            Logger.Info($"最高版本号从 {HighestVersionCode} 升高到 {VersionCode}")
        End If
        '被移除的窗口设置选项
        If Settings.Get(Of Integer)("LaunchArgumentWindowType") = 5 Then Settings.Set("LaunchArgumentWindowType", 1)
        '修改主题设置项名称
        If HighestVersionCode <= 207 Then
            Dim UnlockedTheme As New List(Of String) From {"2"}
            UnlockedTheme.AddRange(New List(Of String)(Settings.Get(Of String)("UiLauncherThemeHide").ToString.Split("|")))
            UnlockedTheme.AddRange(New List(Of String)(Settings.Get(Of String)("UiLauncherThemeHide2").ToString.Split("|")))
            Settings.Set("UiLauncherThemeHide2", UnlockedTheme.Distinct.ToList.Join("|"c))
        End If
        '重置欧皇彩
        If LastVersionCode <= 115 AndAlso Settings.Get(Of String)("UiLauncherThemeHide2").ToString.Split("|").Contains("13") Then
            Dim UnlockedTheme As New List(Of String)(Settings.Get(Of String)("UiLauncherThemeHide2").ToString.Split("|"))
            UnlockedTheme.Remove("13")
            Settings.Set("UiLauncherThemeHide2", UnlockedTheme.Join("|"c))
            MyMsgBox("由于新版 PCL MITC 修改了欧皇彩的解锁方式，你需要重新解锁欧皇彩。" & vbCrLf &
                     "多谢各位的理解啦！", "重新解锁提醒")
        End If
        '重置滑稽彩
        If LastVersionCode <= 152 AndAlso Settings.Get(Of String)("UiLauncherThemeHide2").ToString.Split("|").Contains("12") Then
            Dim UnlockedTheme As New List(Of String)(Settings.Get(Of String)("UiLauncherThemeHide2").ToString.Split("|"))
            UnlockedTheme.Remove("12")
            Settings.Set("UiLauncherThemeHide2", UnlockedTheme.Join("|"c))
            MyMsgBox("由于新版 PCL MITC 修改了滑稽彩的解锁方式，你需要重新解锁滑稽彩。" & vbCrLf &
                     "多谢各位的理解啦！", "重新解锁提醒")
        End If
        '移动自定义皮肤
        If LastVersionCode <= 161 AndAlso FileUtils.Exists(Paths.Base & "MITC\CustomSkin.png") AndAlso Not FileUtils.Exists(Paths.AppDataThenName & "CustomSkin.png") Then
            FileUtils.Copy(Paths.Base & "MITC\CustomSkin.png", Paths.AppDataThenName & "CustomSkin.png")
            Logger.Info("已移动离线自定义皮肤 (162)")
        End If
        If LastVersionCode <= 263 AndAlso FileUtils.Exists(PathTemp & "CustomSkin.png") AndAlso Not FileUtils.Exists(Paths.AppDataThenName & "CustomSkin.png") Then
            FileUtils.Copy(PathTemp & "CustomSkin.png", Paths.AppDataThenName & "CustomSkin.png")
            Logger.Info("已移动离线自定义皮肤 (264)")
        End If
        '解除帮助页面的隐藏
        If LastVersionCode <= 205 Then
            Settings.Set("UiHiddenOtherHelp", False)
            Logger.Info("已解除帮助页面的隐藏")
        End If
        '单向迁移微软登录结果（#4836）
        If Not Settings.Get(Of Boolean)("CacheMsV2Migrated") Then
            Settings.Set("CacheMsV2Migrated", True)
            Settings.Set("CacheMsV2OAuthRefresh", Settings.Get(Of String)("CacheMsOAuthRefresh"))
            Settings.Set("CacheMsV2Access", Settings.Get(Of String)("CacheMsAccess"))
            Settings.Set("CacheMsV2ProfileJson", Settings.Get(Of String)("CacheMsProfileJson"))
            Settings.Set("CacheMsV2Uuid", Settings.Get(Of String)("CacheMsUuid"))
            Settings.Set("CacheMsV2Name", Settings.Get(Of String)("CacheMsName"))
            Logger.Info("已从老版本迁移微软登录结果")
        End If
        'Mod 命名设置迁移
        If Settings.HasSaved("ToolDownloadTranslate") AndAlso Not Settings.HasSaved("ToolDownloadTranslateV2") Then
            Settings.Set("ToolDownloadTranslateV2", Settings.Get(Of Integer)("ToolDownloadTranslate") + 1)
            Logger.Info("已从老版本迁移 Mod 命名设置")
        End If
        '重置 JVM 参数设置
        If LastVersionCode <= 381 AndAlso Settings.HasSaved("LaunchAdvanceJvm") AndAlso
           Settings.Get(Of String)("LaunchAdvanceJvm").ToString.Replace("-XX:+UseG1GC ", "").Replace("-XX:-UseAdaptiveSizePolicy ", "").Trim = Settings.GetDefault("LaunchAdvanceJvm") Then
            Settings.Reset("LaunchAdvanceJvm")
            Logger.Info("已重置 JVM 参数设置")
        End If
        '输出更新日志
        If LastVersionCode <= 0 Then Return
        If HighestVersionCode >= VersionCode Then Return
        ShowUpdateLog(HighestVersionCode)
    End Sub
    Private Sub DowngradeSub(LastVersionCode As Integer)
        Logger.Info($"版本号从 {LastVersionCode} 降低到 {VersionCode}")
        Settings.Set("SystemLastVersionReg", VersionCode)
    End Sub

#End Region

#Region "自定义窗口"

    '关闭
    Private Sub FormMain_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        EndProgram(True)
        e.Cancel = True
    End Sub
    ''' <summary>
    ''' 正常关闭程序。程序将在执行此方法后约 0.3s 退出。
    ''' </summary>
    ''' <param name="SendWarning">是否在还有下载任务未完成时发出警告。</param>
    Public Sub EndProgram(SendWarning As Boolean)
        '强行结束下载任务？
        If HasDownloadingTask() Then
            If SendWarning AndAlso MyMsgBox("还有下载任务尚未完成，是否确定退出？", "提示", "确定", "取消") = 2 Then Return
            RunInNewThread(
            Sub()
                Logger.Info("正在强行停止任务")
                For Each Task As LoaderBase In LoaderTaskbar.ToList()
                    Task.Cancel()
                Next
            End Sub, "强行停止下载任务")
        End If
        '关闭联机？
        If FrmLinkMain?.TryExit(Not SendWarning, True) Then Return
        '关闭
        RunInUiWait(
        Sub()
            IsHitTestVisible = False
            If PanBack.RenderTransform Is Nothing Then
                Dim TransformPos As New TranslateTransform(0, 0)
                Dim TransformRotate As New RotateTransform(0)
                Dim TransformScale As New ScaleTransform(1, 1)
                PanBack.RenderTransform = New TransformGroup() With {.Children = New TransformCollection({TransformRotate, TransformPos, TransformScale})}
                AniStart({
                    AaOpacity(Me, -Opacity, 140, 40, New AniEaseOutFluent(AniEasePower.Weak)),
                    AaDouble(
                    Sub(i)
                        TransformScale.ScaleX += i
                        TransformScale.ScaleY += i
                    End Sub, 0.88 - TransformScale.ScaleX, 180),
                    AaDouble(Sub(i) TransformPos.Y += i, 20 - TransformPos.Y, 180, 0, New AniEaseOutFluent(AniEasePower.Weak)),
                    AaDouble(Sub(i) TransformRotate.Angle += i, 0.6 - TransformRotate.Angle, 180, 0, New AniEaseInoutFluent(AniEasePower.Weak)),
                    AaCode(
                    Sub()
                        IsHitTestVisible = False
                        Top = -10000
                        ShowInTaskbar = False
                    End Sub, 210),
                    AaCode(AddressOf EndProgramForce, 230)
                }, "Form Close")
            Else
                EndProgramForce()
            End If
            Logger.Info("收到关闭指令")
        End Sub)
    End Sub
    Private Shared IsLogShown As Boolean = False
    Public Shared Sub EndProgramForce(Optional ReturnCode As ProcessReturnValues = ProcessReturnValues.Success)
        On Error Resume Next
        IsProgramEnding = True
        AniControlEnabled += 1
        If IsUpdateWaitingRestart Then UpdateRestart(False)
        If ReturnCode = ProcessReturnValues.Exception Then
            If Not IsLogShown Then
                FeedbackInfo()
                Logger.Info("请在 https://github.com/Meloong-Git/PCL/issues 提交错误报告，以便于作者解决此问题！")
                IsLogShown = True
                StartProcess(Paths.Base & "MITC\Log1.txt")
            End If
            Thread.Sleep(500) '防止 PCL 在记事本打开前就被掐掉
        End If
        Logger.Info($"程序已退出，返回值：{ReturnCode}")
        ConfigUtils.SaveAll()
        Logger.Instance.Flush()
        If ReturnCode <> ProcessReturnValues.Success Then Environment.Exit(ReturnCode)
        Process.GetCurrentProcess.Kill()
    End Sub
    Private Sub BtnTitleClose_Click(sender As Object, e As RoutedEventArgs) Handles BtnTitleClose.Click
        EndProgram(True)
    End Sub

    '移动
    Private Sub FormDragMove(sender As Object, e As MouseButtonEventArgs) Handles PanTitle.MouseLeftButtonDown, PanMsg.MouseLeftButtonDown
        On Error Resume Next
        If sender.IsMouseDirectlyOver Then DragMove()
    End Sub

    '改变大小
    ''' <summary>
    ''' 是否可以向注册表储存尺寸改变信息。以此避免初始化时误储存。
    ''' </summary>
    Public IsSizeSaveable As Boolean = False
    Private Sub FormMain_SizeChanged() Handles Me.SizeChanged, Me.Loaded
        If IsSizeSaveable Then
            Settings.Set("WindowHeight", Height)
            Settings.Set("WindowWidth", Width)
        End If
        RectForm.Rect = New Rect(0, 0, BorderForm.ActualWidth, BorderForm.ActualHeight)
        PanForm.Width = BorderForm.ActualWidth + 0.001
        PanForm.Height = BorderForm.ActualHeight + 0.001
        PanMain.Width = PanForm.Width
        PanMain.Height = Math.Max(0, PanForm.Height - PanTitle.ActualHeight)
        If WindowState = WindowState.Maximized Then WindowState = WindowState.Normal '修复 #1938
    End Sub

    '最小化
    Private Sub BtnTitleMin_Click() Handles BtnTitleMin.Click
        WindowState = WindowState.Minimized
    End Sub

    '背景图片与标题栏
    Public Shared Sub UpdateBackgroundAndTitleBar(Value) '从设置更新
        If FrmMain Is Nothing OrElse Not FrmMain.IsLoaded Then Return
        FrmMain.UpdateBackgroundAndTitleBar()
    End Sub
    Public Sub UpdateBackgroundAndTitleBar()
        Logger.Info("从设置更新背景图片与标题栏样式")
        '背景图片透明度
        ImgBack.Opacity = Settings.Get(Of Integer)("UiBackgroundOpacity") / 1000
        '背景图片模糊
        Dim BlurRadius As Double = Settings.Get(Of Integer)("UiBackgroundBlur") + 1
        If BlurRadius = 1 Then
            ImgBack.Effect = Nothing
        Else
            ImgBack.Effect = New Effects.BlurEffect With {.Radius = BlurRadius}
        End If
        ImgBack.Margin = New Thickness(-BlurRadius / 1.8)
        '背景图片适应方式
        Dim BackgroundType As Integer = Settings.Get(Of Integer)("UiBackgroundSuit")
        If ImgBack.Background IsNot Nothing Then
            Dim Brush As ImageBrush = CType(ImgBack.Background, ImageBrush)
            If BackgroundType = 0 Then
                '智能：当图片较小时平铺，较大时适应
                If Brush.ImageSource.Width < PanMain.ActualWidth / 2 AndAlso Brush.ImageSource.Height < PanMain.ActualHeight / 2 Then
                    BackgroundType = 4 '平铺
                Else
                    BackgroundType = 2 '适应
                End If
            End If
            Brush.Stretch = Stretch.UniformToFill
            Brush.TileMode = TileMode.None
            Brush.Viewport = New Rect(0, 0, 1, 1)
            Brush.ViewportUnits = BrushMappingMode.RelativeToBoundingBox
            Brush.AlignmentX = AlignmentX.Center
            Brush.AlignmentY = AlignmentY.Center
            Select Case BackgroundType
                Case 1 '中
                    Brush.Stretch = Stretch.None
                Case 3 '拉伸
                    Brush.Stretch = Stretch.Fill
                Case 4 '平铺
                    Brush.Stretch = Stretch.None
                    Brush.TileMode = TileMode.Tile
                    Brush.Viewport = New Rect(0, 0, Brush.ImageSource.Width, Brush.ImageSource.Height)
                    Brush.ViewportUnits = BrushMappingMode.Absolute

                Case 2 '适应
                Case 5 '左上
                    Brush.AlignmentX = AlignmentX.Left
                    Brush.AlignmentY = AlignmentY.Top
                Case 6 '右上
                    Brush.AlignmentX = AlignmentX.Right
                    Brush.AlignmentY = AlignmentY.Top
                Case 7 '左下
                    Brush.AlignmentX = AlignmentX.Left
                    Brush.AlignmentY = AlignmentY.Bottom
                Case 8 '右下
                    Brush.AlignmentX = AlignmentX.Right
                    Brush.AlignmentY = AlignmentY.Bottom
                Case 9 '左
                    Brush.AlignmentX = AlignmentX.Left
                    Brush.AlignmentY = AlignmentY.Center
                Case 10 '右
                    Brush.AlignmentX = AlignmentX.Right
                    Brush.AlignmentY = AlignmentY.Center
                Case 11 '上
                    Brush.AlignmentX = AlignmentX.Center
                    Brush.AlignmentY = AlignmentY.Top
                Case 12 '下
                    Brush.AlignmentX = AlignmentX.Center
                    Brush.AlignmentY = AlignmentY.Bottom
            End Select
        End If
        '标题栏显示类型
        Select Case Settings.Get(Of Integer)("UiLogoType")
            Case 0 '无
                ShapeTitleLogo.Visibility = Visibility.Collapsed
                BorTitleLogo.Visibility = Visibility.Collapsed
                ImageTitleLogo.Visibility = Visibility.Collapsed
                If Not IsNothing(FrmSetupUI) Then 'TODO: 和 FrmSetupUI 解耦
                    FrmSetupUI.CheckLogoLeft.Visibility = Visibility.Visible
                    FrmSetupUI.PanLogoText.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoChange.Visibility = Visibility.Collapsed
                End If
            Case 1 '默认
                ShapeTitleLogo.Visibility = Visibility.Collapsed
                BorTitleLogo.Visibility = Visibility.Visible
                ImageTitleLogo.Visibility = Visibility.Collapsed
                If Not IsNothing(FrmSetupUI) Then
                    FrmSetupUI.CheckLogoLeft.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoText.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoChange.Visibility = Visibility.Collapsed
                End If
            Case 2 '文本
                ShapeTitleLogo.Visibility = Visibility.Collapsed
                BorTitleLogo.Visibility = Visibility.Visible
                ImageTitleLogo.Visibility = Visibility.Collapsed
                If Not IsNothing(FrmSetupUI) Then
                    FrmSetupUI.CheckLogoLeft.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoText.Visibility = Visibility.Visible
                    FrmSetupUI.PanLogoChange.Visibility = Visibility.Collapsed
                End If
            Case 3 '图片
                ShapeTitleLogo.Visibility = Visibility.Collapsed
                BorTitleLogo.Visibility = Visibility.Collapsed
                ImageTitleLogo.Visibility = Visibility.Visible
                If Not IsNothing(FrmSetupUI) Then
                    FrmSetupUI.CheckLogoLeft.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoText.Visibility = Visibility.Collapsed
                    FrmSetupUI.PanLogoChange.Visibility = Visibility.Visible
                End If
                Try
                    ImageTitleLogo.Source = Paths.Base & "MITC\Logo.png"
                Catch ex As Exception
                    ImageTitleLogo.Source = Nothing
                    Logger.Error(ex, "显示标题栏图片失败", LogBehavior.Alert)
                End Try
        End Select
        FrmSetupUI?.CardLogo.TriggerForceResize()
        '标题栏文本
        LabTitleLogo.Text = If(String.IsNullOrWhiteSpace(Settings.Get(Of String)("UiLogoText")), "PCL MITC", Settings.Get(Of String)("UiLogoText"))
        '标题栏文本是否居左
        PanTitleMain.ColumnDefinitions(0).Width = New GridLength(If(Settings.Get(Of Boolean)("UiLogoLeft") AndAlso Settings.Get(Of Integer)("UiLogoType") = 0, 0, 1), GridUnitType.Star)
    End Sub

#End Region

#Region "窗体事件"

    '按键事件
    Private Sub FormMain_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        If e.IsRepeat Then Return
        '修复按下 Alt 后误认为弹出系统菜单导致的冻结
        If e.SystemKey = Key.LeftAlt OrElse e.SystemKey = Key.RightAlt Then e.Handled = True
        '在有弹窗时：回车选择第一个，Esc 选择最后一个
        If PanMsg.Children.Any Then
            If e.Key = Key.Enter Then
                CType(PanMsg.Children(0), Object).Btn1_Click()
            ElseIf e.Key = Key.Escape Then
                Dim Msg As Object = PanMsg.Children(0)
                If TypeOf Msg IsNot MyMsgInput AndAlso TypeOf Msg IsNot MyMsgSelect AndAlso Msg.Btn3.Visibility = Visibility.Visible Then
                    Msg.Btn3_Click()
                ElseIf Msg.Btn2.Visibility = Visibility.Visible Then
                    Msg.Btn2_Click()
                Else
                    Msg.Btn1_Click()
                End If
            End If
            Return
        End If

        '==========================
        ' 在没有弹窗时：继续检查……
        '==========================

        '按 ESC 返回上一级
        If e.Key = Key.Escape Then TriggerPageBack()
        '更改隐藏版本可见性
        If e.Key = Key.F11 AndAlso PageCurrent = FormMain.PageType.InstanceSelect Then
            FrmSelectRight.ShowHidden = Not FrmSelectRight.ShowHidden
            LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.ForceRun, MaxDepth:=1, ExtraPath:="versions\")
            Return
        End If
        '更改功能隐藏可见性
        If e.Key = Key.F12 Then
            PageSetupUI.HiddenForceShow = Not PageSetupUI.HiddenForceShow
            If PageSetupUI.HiddenForceShow Then
                Hint("功能隐藏设置已暂时关闭！", HintType.Green)
            Else
                Hint("功能隐藏设置已重新开启！", HintType.Green)
            End If
            PageSetupUI.HiddenRefresh()
            Return
        End If
        '按 F5 刷新页面
        If e.Key = Key.F5 Then
            If TypeOf PageLeft Is IRefreshable Then CType(PageLeft, IRefreshable).Refresh()
            If TypeOf PageRight Is IRefreshable Then CType(PageRight, IRefreshable).Refresh()
            Return
        End If
        '调用启动游戏
        If e.Key = Key.Enter AndAlso PageCurrent = FormMain.PageType.Launch Then
            If IsAprilEnabled AndAlso Not IsAprilGiveup Then
                Hint("木大！")
            Else
                FrmLaunchLeft.LaunchButtonClick()
            End If
        End If
    End Sub
    Private Sub FormMain_MouseDown(sender As Object, e As MouseButtonEventArgs) Handles Me.MouseDown
        '鼠标侧键返回上一级
        If FrmMain.PanMsg.Children.Count > 0 OrElse WaitingMyMsgBox.Any Then Return '弹窗中（#5513）
        If e.ChangedButton = MouseButton.XButton1 OrElse e.ChangedButton = MouseButton.XButton2 Then TriggerPageBack()
    End Sub
    Private Sub TriggerPageBack()
        If PageCurrent = PageType.Download AndAlso PageCurrentSub = PageSubType.DownloadInstall AndAlso FrmDownloadInstall.IsInSelectPage Then
            FrmDownloadInstall.ExitSelectPage()
        Else
            PageBack()
        End If
    End Sub

    '切回窗口
    Private Sub FormMain_Activated() Handles Me.Activated
        '切回窗口时自动刷新
        Try
            If PageCurrent = PageType.InstanceSetup AndAlso PageCurrentSub = PageSubType.InstanceMod Then
                'Mod 管理
                FrmInstanceMod.ReloadModList()
            ElseIf PageCurrent = PageType.InstanceSetup AndAlso (PageCurrentSub = PageSubType.InstanceSetup OrElse PageCurrentSub = PageSubType.InstanceExport) Then
                '更新当前选用的 Java
                PageInstanceLeft.ReloadCurrentJava()
            ElseIf PageCurrent = PageType.InstanceSelect Then
                '版本选择
                LoaderFolderRun(McInstanceListLoader, McFolderSelected, LoaderFolderRunType.RunOnUpdated, MaxDepth:=1, ExtraPath:="versions\")
            End If
        Catch ex As Exception
            Logger.Error(ex, "切回窗口时出错")
        End Try
        '读取剪贴板，自动加入联机房间
        Return 'TODO: 联机复活赛
        If PageLinkMain.LinkState <> PageLinkMain.LinkStates.Waiting Then Return '已启动联机
        If PageCurrent = PageType.Link Then Return '已在联机界面
        Dim Code = ClipboardGetText() : If Code Is Nothing Then Return '剪贴板无文本
        If Settings.Get(Of String)("LinkLastAutoJoinInviteCode") = Code Then Return
        If PageLinkMain.ValidateCodeFormat(Code) IsNot Nothing Then Return '不是邀请码
        Settings.Set("LinkLastAutoJoinInviteCode", Code)
        RunInThread(
        Sub()
            If MyMsgBox("嘿，是否使用复制的邀请码加入房间？", "加入联机房间", "加入", "取消") = 2 Then Return '防止弹窗阻碍主线程，所以必须放在工作线程
            RunInUi(
            Sub()
                PageLinkMain.Join(Code)
                ClipboardSet(Nothing, False)
            End Sub)
        End Sub)
    End Sub

    '文件拖放
    Private Sub HandleDrag(sender As Object, e As DragEventArgs)
        Try
            If e.Handled AndAlso (e.Effects <> DragDropEffects.None) Then Return
            e.Handled = True
            '缓存
            Static PrevData As IDataObject, PrevEffects As DragDropEffects
            If e.Data Is PrevData Then
                e.Effects = PrevEffects
                Return
            End If
            '确定拖放效果
            e.Effects = DragDropEffects.None
            If e.Data.GetDataPresent(DataFormats.Text) Then
                Dim Str As String = e.Data.GetData(DataFormats.Text)
                If Str.StartsWithF("authlib-injector:yggdrasil-server:") Then
                    e.Effects = DragDropEffects.Copy
                ElseIf Str.StartsWithF("file:///") Then
                    e.Effects = DragDropEffects.Copy
                End If
            ElseIf e.Data.GetDataPresent(DataFormats.FileDrop) Then
                Dim Files As String() = e.Data.GetData(DataFormats.FileDrop)
                If Files IsNot Nothing AndAlso Files.Length > 0 Then
                    e.Effects = DragDropEffects.Link
                End If
            End If
            PrevData = e.Data
            PrevEffects = e.Effects
            Logger.Info($"设置拖放类型：{e.Effects}")
        Catch ex As Exception
            Logger.Error(ex, "处理拖放时出错")
        End Try
    End Sub
    Private Sub FrmMain_Drop(sender As Object, e As DragEventArgs) Handles Me.Drop
        Try
            ShowWindowToTop()
            If e.Data.GetDataPresent(DataFormats.Text) Then
                '获取文本
                Try
                    Dim Str As String = e.Data.GetData(DataFormats.Text)
                    Logger.Info($"接受文本拖拽：{Str}")
                    If Str.StartsWithF("authlib-injector:yggdrasil-server:") Then
                        'Authlib 拖拽
                        e.Handled = True
                        e.Effects = DragDropEffects.Copy
                        Dim AuthlibServer As String = StringUtils.FormUrlUnescape(Str.Substring("authlib-injector:yggdrasil-server:".Length))
                        Logger.Info($"Authlib 拖拽：{AuthlibServer}")
                        If Not String.IsNullOrEmpty(New ValidateHttp().Validate(AuthlibServer)) Then
                            Hint($"输入的 Authlib 验证服务器不符合网址格式（{AuthlibServer}）！", HintType.Red)
                            Return
                        End If
                        Dim Target = If(PageCurrent = PageType.InstanceSetup, PageInstanceLeft.Instance, McInstanceSelected)
                        If Target Is Nothing Then
                            Hint("请先下载游戏，再设置第三方登录！", HintType.Red)
                            Return
                        End If
                        If AuthlibServer = "https://littleskin.cn/api/yggdrasil" Then
                            'LittleSkin
                            If AuthlibDropAcceptedServer <> AuthlibServer Then
                                If MyMsgBox($"是否要在版本 {Target.Name} 中开启 LittleSkin 登录？" & vbCrLf &
                                            "你可以在 版本设置 → 设置 → 服务器选项 中修改登录方式。", "第三方登录开启确认", "确定", "取消") = 2 Then
                                    Return
                                End If
                                AuthlibDropAcceptedServer = AuthlibServer
                            End If
                            Settings.Set("VersionServerLogin", 4, Instance:=Target)
                            Settings.Set("VersionServerAuthServer", "https://littleskin.cn/api/yggdrasil", Instance:=Target)
                            Settings.Set("VersionServerAuthRegister", "https://littleskin.cn/auth/register", Instance:=Target)
                            Settings.Set("VersionServerAuthName", "LittleSkin 登录", Instance:=Target)
                        Else
                            '第三方 Authlib 服务器
                            If AuthlibDropAcceptedServer <> AuthlibServer Then
                                If MyMsgBox($"是否要在版本 {Target.Name} 中开启第三方登录？" & vbCrLf &
                                            $"登录服务器：{AuthlibServer}" & vbCrLf & vbCrLf &
                                            "你可以在 版本设置 → 设置 → 服务器选项 中修改登录方式。", "第三方登录开启确认", "确定", "取消") = 2 Then
                                    Return
                                End If
                                AuthlibDropAcceptedServer = AuthlibServer
                            End If
                            Settings.Set("VersionServerLogin", 4, Instance:=Target)
                            Settings.Set("VersionServerAuthServer", AuthlibServer, Instance:=Target)
                            Settings.Set("VersionServerAuthRegister", AuthlibServer.Replace("api/yggdrasil", "auth/register"), Instance:=Target)
                            Settings.Set("VersionServerAuthName", "", Instance:=Target)
                        End If
                        If PageCurrent = PageType.InstanceSetup AndAlso PageCurrentSub = PageSubType.InstanceSetup Then
                            '正在服务器选项页，需要刷新设置项显示
                            FrmInstanceSetup.Reload()
                        ElseIf PageCurrent = PageType.Launch Then
                            '正在主页，需要刷新左边栏
                            FrmLaunchLeft.RefreshPage(True, False)
                        End If
                    ElseIf Str.StartsWithF("file:///") Then
                        '文件拖拽（例如从浏览器下载窗口拖入）
                        Dim FilePath = Net.WebUtility.UrlDecode(Str).Substring("file:///".Length).Replace("/", "\")
                        e.Handled = True
                        e.Effects = DragDropEffects.Copy
                        FileDrag(New List(Of String) From {FilePath})
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "无法接取文本拖拽事件")
                    Return
                End Try
            ElseIf e.Data.GetDataPresent(DataFormats.FileDrop) Then
                '获取文件并检查
                Dim FilePathRaw = e.Data.GetData(DataFormats.FileDrop)
                If FilePathRaw Is Nothing Then '#2690
                    Hint("请将文件解压后再拖入！", HintType.Red)
                    Return
                End If
                e.Handled = True
                e.Effects = DragDropEffects.Link
                FileDrag(CType(FilePathRaw, IEnumerable(Of String)))
            End If
        Catch ex As Exception
            Logger.Error(ex, "接取拖拽事件失败")
        End Try
    End Sub
    Private Sub FileDrag(FilePathList As IEnumerable(Of String))
        RunInNewThread(
        Sub()
            Dim FilePath As String = FilePathList.First
            Logger.Warn($"接受文件拖拽：{FilePath}{If(FilePathList.Any, $" 等 {FilePathList.Count} 个文件", "")}")
            '基础检查
            If DirectoryUtils.Exists(FilePathList.First) AndAlso Not FileUtils.Exists(FilePathList.First) Then
                Hint("请拖入一个文件，而非文件夹！", HintType.Red)
                Return
            ElseIf Not FileUtils.Exists(FilePathList.First) Then
                Hint("拖入的文件不存在：" & FilePathList.First, HintType.Red)
                Return
            End If
            '多文件拖拽
            Dim PathList As List(Of String) = FilePathList.ToList()
            If FilePathList.Count > 1 Then
                '必须要求全部为 jar 文件
                For Each File In PathList
                    If Not {"jar", "litemod", "disabled", "old"}.Contains(File.AfterLast(".").Lower) Then
                        Hint("一次请只拖入一个文件！", HintType.Red)
                        Return
                    End If
                Next
            End If
            '主页
            Dim Extension As String = FilePath.AfterLast(".").Lower
            If Extension = "xaml" Then
                Logger.Info("文件后缀为 XAML，作为主页加载")
                If FileUtils.Exists(Paths.Base & "MITC\Custom.xaml") Then
                    If MyMsgBox("已存在一个主页文件，是否要将它覆盖？", "覆盖确认", "覆盖", "取消") = 2 Then
                        Return
                    End If
                End If
                FileUtils.Copy(FilePath, Paths.Base & "MITC\Custom.xaml")
                RunInUi(
                Sub()
                    Settings.Set("UiCustomType", 1)
                    FrmLaunchRight.ForceRefresh()
                    Hint("已加载主页自定义文件！", HintType.Green)
                End Sub)
                Return
            End If
            '安装 Mod
            If PageInstanceMod.InstallMods(PathList) Then Return
            '安装整合包
            If {"zip", "rar", "mrpack"}.Any(Function(t) t = Extension) Then '部分压缩包是 zip 格式但后缀为 rar，总之试一试
                Logger.Info("文件为压缩包，尝试作为整合包安装")
                Try
                    ModpackInstall(FilePath)
                    Return
                Catch ex As Exception
                    If ex.IsCanceled Then Return
                    '安装失败，继续往后尝试
                End Try
            End If
            'RAR 处理
            If Extension = "rar" Then
                Hint("PCL MITC 无法处理 rar 格式的压缩包，请在解压后重新压缩为 zip 格式再试！")
                Return
            End If
            '错误报告分析
            Try
                Logger.Info("尝试进行错误报告分析")
                Dim Analyzer As New CrashAnalyzer
                Analyzer.Import(FilePath)
                If Not Analyzer.Prepare() Then Exit Try
                Analyzer.Analyze()
                Analyzer.Output(True, New List(Of String))
                Return
            Catch ex As Exception
                Logger.Error(ex, "自主错误报告分析失败")
            End Try
            '未知操作
            Hint("PCL MITC 无法确定应当执行的文件拖拽操作……")
        End Sub, "文件拖拽")
    End Sub

    '接受到 Windows 窗体事件
    Public IsSystemTimeChanged As Boolean = False
    Private Function WndProc(hwnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr, ByRef handled As Boolean) As IntPtr
        If msg = 30 Then
            Dim NowDate = Date.Now
            If NowDate.Date = ApplicationOpenTime.Date Then
                Logger.Info($"系统时间微调为：{NowDate.ToLongDateString} {NowDate.ToLongTimeString}")
                IsSystemTimeChanged = False
            Else
                Logger.Info($"系统时间修改为：{NowDate.ToLongDateString} {NowDate.ToLongTimeString}")
                IsSystemTimeChanged = True
            End If
        ElseIf msg = 400 * 16 + 2 Then
            Logger.Info($"收到置顶信息：{hwnd.ToInt64}")
            If Not IsWindowLoadFinished Then
                Logger.Info("窗口尚未加载完成，忽略置顶请求")
                Return IntPtr.Zero
            End If
            ShowWindowToTop()
            handled = True
        End If
        Return IntPtr.Zero
    End Function

    '窗口隐藏与置顶
    Private _Hidden As Boolean = False
    Public Property Hidden As Boolean
        Get
            Return _Hidden
        End Get
        Set(value As Boolean)
            If _Hidden = value Then Return
            _Hidden = value
            If value Then
                '隐藏
                Left -= 10000
                ShowInTaskbar = False
                Visibility = Visibility.Hidden
                Logger.Info($"窗口已隐藏，位置：({Left},{Top})")
            Else
                '取消隐藏
                If Left < -2000 Then Left += 10000
                ShowWindowToTop()
            End If
        End Set
    End Property
    ''' <summary>
    ''' 把当前窗口拖到最前面。
    ''' </summary>
    Public Sub ShowWindowToTop()
        RunInUi(
        Sub()
            '这一坨乱七八糟的，别改，改了指不定就炸了，自己电脑还复现不出来
            Visibility = Visibility.Visible
            ShowInTaskbar = True
            WindowState = WindowState.Normal
            Hidden = False
            Topmost = True '偶尔 SetForegroundWindow 失效
            Topmost = False
            SetForegroundWindow(Handle)
            Focus()
            Logger.Info($"窗口已置顶，位置：({Left}, {Top}), {Width} x {Height}")
        End Sub)
    End Sub

#End Region

#Region "切换页面"

    '页面种类与属性
    ''' <summary>
    ''' 页面种类。
    ''' 该枚举在自定义事件中使用，是公开 API 的一部分。
    ''' </summary>
    Public Enum PageType
        ''' <summary>
        ''' 启动。
        ''' </summary>
        Launch = 0
        ''' <summary>
        ''' 下载。
        ''' </summary>
        Download = 1
        ''' <summary>
        ''' 联机。
        ''' </summary>
        Link = 2
        ''' <summary>
        ''' 设置。
        ''' </summary>
        Setup = 3
        ''' <summary>
        ''' 更多。
        ''' </summary>
        Other = 4
        ''' <summary>
        ''' 版本选择。这是一个副页面。
        ''' </summary>
        InstanceSelect = 5
        ''' <summary>
        ''' 下载管理。这是一个副页面。
        ''' </summary>
        DownloadManager = 6
        ''' <summary>
        ''' 版本设置。这是一个副页面。
        ''' </summary>
        InstanceSetup = 7
        ''' <summary>
        ''' 社区资源详情。这是一个副页面。
        ''' </summary>
        ResourceDetail = 8
        ''' <summary>
        ''' 帮助详情。这是一个副页面。
        ''' </summary>
        HelpDetail = 9
    End Enum
    ''' <summary>
    ''' 次要页面种类。其数值必须与 StackPanel 中的下标一致。
    ''' 该枚举在自定义事件中使用，是公开 API 的一部分。
    ''' </summary>
    Public Enum PageSubType
        [Default] = 0
        DownloadInstall = 0
        DownloadMod = 2
        DownloadPack = 3
        DownloadDataPack = 4
        DownloadResourcePack = 5
        DownloadShader = 6
        SetupLaunch = 0
        SetupLink = 1
        SetupUI = 2
        SetupSystem = 3
        SetupLicense = 4
        LinkMain = 0
        OtherHelp = 0
        OtherAbout = 1
        OtherTest = 2
        InstanceOverall = 0
        InstanceSetup = 1
        InstanceMod = 2
        InstanceModDisabled = 3
        InstanceExport = 4
    End Enum
    ''' <summary>
    ''' 获取次级页面的名称。若并非次级页面则返回空字符串，故可以以此判断是否为次级页面。
    ''' </summary>
    Private Function PageNameGet(Stack As PageStackData) As String
        Select Case Stack.Page
            Case PageType.InstanceSelect
                Return "版本选择"
            Case PageType.DownloadManager
                Return "下载管理"
            Case PageType.InstanceSetup
                Return "版本设置 - " & If(PageInstanceLeft.Instance Is Nothing, "未知版本", PageInstanceLeft.Instance.Name)
            Case PageType.ResourceDetail
                Return CType(Stack.Additional(0), ResourceProject).TranslatedName
            Case PageType.HelpDetail
                Return CType(Stack.Additional(0), HelpEntry).Title
            Case Else
                Return ""
        End Select
    End Function
    ''' <summary>
    ''' 刷新次级页面的名称。
    ''' </summary>
    Public Sub PageNameRefresh(Type As PageStackData)
        LabTitleInner.Text = PageNameGet(Type)
    End Sub
    ''' <summary>
    ''' 刷新次级页面的名称。
    ''' </summary>
    Public Sub PageNameRefresh()
        PageNameRefresh(PageCurrent)
    End Sub

    '页面状态存储
    ''' <summary>
    ''' 当前的主页面。
    ''' </summary>
    ''' <summary>
    ''' 启动后是否自动打开联机页（由 --link 参数设置）。
    ''' </summary>
    Public Shared StartupOpenLink As Boolean = False

    ''' <summary>本次运行内已确认过的 Authlib 拖拽服务器，同一个不再重复弹窗。</summary>
    Private Shared AuthlibDropAcceptedServer As String = Nothing
    Public PageCurrent As PageStackData = PageType.Launch
    ''' <summary>
    ''' 上一个主页面。
    ''' </summary>
    Public PageLast As PageStackData = PageType.Launch
    ''' <summary>
    ''' 当前的子页面。
    ''' </summary>
    Public ReadOnly Property PageCurrentSub As PageSubType
        Get
            Select Case PageCurrent
                Case PageType.Download
                    If FrmDownloadLeft Is Nothing Then FrmDownloadLeft = New PageDownloadLeft
                    Return FrmDownloadLeft.PageID
                Case PageType.Setup
                    If FrmSetupLeft Is Nothing Then FrmSetupLeft = New PageSetupLeft
                    Return FrmSetupLeft.PageID
                Case PageType.Other
                    If FrmOtherLeft Is Nothing Then FrmOtherLeft = New PageOtherLeft
                    Return FrmOtherLeft.PageID
                Case PageType.InstanceSetup
                    If FrmInstanceLeft Is Nothing Then FrmInstanceLeft = New PageInstanceLeft
                    Return FrmInstanceLeft.PageID
                Case Else
                    Return 0 '没有子页面
            End Select
        End Get
    End Property
    ''' <summary>
    ''' 上层页面的编号堆栈，用于返回。
    ''' </summary>
    Public PageStack As New List(Of PageStackData)
    Public Class PageStackData

        Public Page As PageType
        Public Additional As Object

        Public Overrides Function Equals(other As Object) As Boolean
            If other Is Nothing Then Return False
            If TypeOf other Is PageStackData Then
                Dim PageOther As PageStackData = other
                If Page <> PageOther.Page Then Return False
                If Additional Is Nothing Then
                    Return PageOther.Additional Is Nothing
                Else
                    Return PageOther.Additional IsNot Nothing AndAlso Additional.Equals(PageOther.Additional)
                End If
            ElseIf TypeOf other Is Integer Then
                If Page <> other Then Return False
                Return Additional Is Nothing
            Else
                Return False
            End If
        End Function
        Public Shared Operator =(left As PageStackData, right As PageStackData) As Boolean
            Return EqualityComparer(Of PageStackData).Default.Equals(left, right)
        End Operator
        Public Shared Operator <>(left As PageStackData, right As PageStackData) As Boolean
            Return Not left = right
        End Operator
        Public Shared Widening Operator CType(Value As PageType) As PageStackData
            Return New PageStackData With {.Page = Value}
        End Operator
        Public Shared Widening Operator CType(Value As PageStackData) As PageType
            Return Value.Page
        End Operator
    End Class
    Public PageLeft As MyPageLeft, PageRight As MyPageRight

    '引发实际页面切换的入口
    Private IsChangingPage As Boolean = False
    ''' <summary>
    ''' 切换页面，并引起对应选择 UI 的改变。
    ''' </summary>
    Public Sub PageChange(Stack As PageStackData, Optional SubType As PageSubType = PageSubType.Default)
        If PageNameGet(Stack) = "" Then
            '切换到主页面
            PageChangeExit()
            IsChangingPage = True '防止下面的勾选直接触发了 PageChangeActual
            CType(PanTitleSelect.Children(Stack), MyRadioButton).SetChecked(True, True, PageNameGet(PageCurrent) = "")
            IsChangingPage = False
            Select Case Stack.Page
                Case PageType.Download
                    If FrmDownloadLeft Is Nothing Then FrmDownloadLeft = New PageDownloadLeft
                    CType(FrmDownloadLeft.PanItem.Children(SubType), MyListItem).SetChecked(True, True, Stack = PageCurrent)
                Case PageType.Setup
                    If FrmSetupLeft Is Nothing Then FrmSetupLeft = New PageSetupLeft
                    CType(FrmSetupLeft.PanItem.Children(SubType), MyListItem).SetChecked(True, True, Stack = PageCurrent)
                Case PageType.Other
                    If FrmOtherLeft Is Nothing Then FrmOtherLeft = New PageOtherLeft
                    CType(FrmOtherLeft.PanItem.Children(SubType), MyListItem).SetChecked(True, True, Stack = PageCurrent)
            End Select
            PageChangeActual(Stack, SubType)
        Else
            '切换到次页面
            Select Case Stack.Page
                Case PageType.InstanceSetup
                    If FrmInstanceLeft Is Nothing Then FrmInstanceLeft = New PageInstanceLeft
                    CType(FrmInstanceLeft.PanItem.Children(SubType), MyListItem).SetChecked(True, True, Stack = PageCurrent)
            End Select
            PageChangeActual(Stack, SubType)
        End If
    End Sub
    ''' <summary>
    ''' 通过点击导航栏改变页面。
    ''' </summary>
    Private Sub BtnTitleSelect_Click(sender As MyRadioButton, raiseByMouse As Boolean) Handles BtnTitleSelect0.Check, BtnTitleSelect1.Check, BtnTitleSelect2.Check, BtnTitleSelect3.Check, BtnTitleSelect4.Check
        If IsChangingPage Then Return
        PageChangeActual(Val(sender.Tag))
    End Sub
    ''' <summary>
    ''' 通过点击返回按钮或手动触发返回来改变页面。
    ''' </summary>
    Public Sub PageBack() Handles BtnTitleInner.Click
        If PageStack.Any() Then
            PageChangeActual(PageStack(0))
        Else
            PageChange(PageType.Launch)
        End If
    End Sub

    '实际处理页面切换
    ''' <summary>
    ''' 切换现有页面的实际方法。
    ''' </summary>
    Private Sub PageChangeActual(Stack As PageStackData, Optional SubType As PageSubType = -1)
        If PageCurrent = Stack AndAlso (PageCurrentSub = SubType OrElse SubType = -1) Then Return
        AniControlEnabled += 1
        Try

#Region "子页面处理"
            Dim PageName As String = PageNameGet(Stack)
            If PageName = "" Then
                '即将切换到一个顶级页面
                PageChangeExit()
            Else
                '即将切换到一个子页面
                If PageStack.Any Then
                    '子页面 → 另一个子页面，更新
                    AniStart({
                        AaOpacity(LabTitleInner, -LabTitleInner.Opacity, 130),
                        AaCode(Sub() LabTitleInner.Text = PageName,, True),
                        AaOpacity(LabTitleInner, 1, 150, 30)
                    }, "FrmMain Titlebar SubLayer")
                    If PageStack.Contains(Stack) Then
                        '返回到更上层的子页面
                        Do While PageStack.Contains(Stack)
                            PageStack.RemoveAt(0)
                        Loop
                    Else
                        '进入更深层的子页面
                        PageStack.Insert(0, PageCurrent)
                    End If
                Else
                    '主页面 → 子页面，进入
                    PanTitleInner.Visibility = Visibility.Visible
                    PanTitleMain.IsHitTestVisible = False
                    PanTitleInner.IsHitTestVisible = True
                    PageNameRefresh(Stack)
                    AniStart({
                        AaOpacity(PanTitleMain, -PanTitleMain.Opacity, 150),
                        AaX(PanTitleMain, 12 - PanTitleMain.Margin.Left, 150,, New AniEaseInFluent(AniEasePower.Weak)),
                        AaOpacity(PanTitleInner, 1 - PanTitleInner.Opacity, 150, 200),
                        AaX(PanTitleInner, -PanTitleInner.Margin.Left, 350, 200, New AniEaseOutBack),
                        AaCode(Sub() PanTitleMain.Visibility = Visibility.Collapsed,, True)
                    }, "FrmMain Titlebar FirstLayer")
                    PageStack.Insert(0, PageCurrent)
                End If
            End If
#End Region

#Region "实际更改页面框架 UI"
            PageLast = PageCurrent
            PageCurrent = Stack
            Select Case Stack.Page
                Case PageType.Launch '启动
                    PageChangeAnim(FrmLaunchLeft, FrmLaunchRight)
                Case PageType.Download '下载
                    If FrmDownloadLeft Is Nothing Then FrmDownloadLeft = New PageDownloadLeft
                    'PageGet 方法会在未设置 SubType 时指定默认值，并建立相关页面的实例
                    PageChangeAnim(FrmDownloadLeft, FrmDownloadLeft.PageGet(SubType))
                Case PageType.Link '联机
                    If FrmLinkMain Is Nothing Then FrmLinkMain = New PageLinkMain
                    PageChangeAnim(New MyPageLeft, FrmLinkMain)
                Case PageType.Setup '设置
                    If FrmSetupLeft Is Nothing Then FrmSetupLeft = New PageSetupLeft
                    PageChangeAnim(FrmSetupLeft, FrmSetupLeft.PageGet(SubType))
                Case PageType.Other '更多
                    If FrmOtherLeft Is Nothing Then FrmOtherLeft = New PageOtherLeft
                    PageChangeAnim(FrmOtherLeft, FrmOtherLeft.PageGet(SubType))
                Case PageType.InstanceSelect '版本选择
                    If FrmSelectLeft Is Nothing Then FrmSelectLeft = New PageSelectLeft
                    If FrmSelectRight Is Nothing Then FrmSelectRight = New PageSelectRight
                    PageChangeAnim(FrmSelectLeft, FrmSelectRight)
                Case PageType.DownloadManager '下载管理
                    If FrmSpeedLeft Is Nothing Then FrmSpeedLeft = New PageSpeedLeft
                    If FrmSpeedRight Is Nothing Then FrmSpeedRight = New PageSpeedRight
                    PageChangeAnim(FrmSpeedLeft, FrmSpeedRight)
                Case PageType.InstanceSetup '版本设置
                    If FrmInstanceLeft Is Nothing Then FrmInstanceLeft = New PageInstanceLeft
                    PageChangeAnim(FrmInstanceLeft, FrmInstanceLeft.PageGet(SubType))
                Case PageType.ResourceDetail '社区资源详情
                    If FrmDownloadResourceDetail Is Nothing Then FrmDownloadResourceDetail = New PageDownloadResourceDetail
                    PageChangeAnim(New MyPageLeft, FrmDownloadResourceDetail)
                Case PageType.HelpDetail '帮助详情
                    PageChangeAnim(New MyPageLeft, Stack.Additional(1))
            End Select
#End Region

#Region "设置为最新状态"
            BtnExtraDownload.ShowRefresh()
            BtnExtraApril.ShowRefresh()
#End Region

            Logger.Info($"切换主要页面：{Stack}, {SubType}")
        Catch ex As Exception
            Logger.Error(ex, $"切换主要页面失败（ID {PageCurrent.Page}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub
    Private Sub PageChangeAnim(TargetLeft As FrameworkElement, TargetRight As FrameworkElement)
        AniStop("FrmMain LeftChange")
        AniStop("PageLeft PageChange") '停止左边栏变更导致的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        AniControlEnabled += 1
        '清除新页面关联性
        If Not IsNothing(TargetLeft.Parent) Then TargetLeft.SetValue(ContentPresenter.ContentProperty, Nothing)
        If Not IsNothing(TargetRight) AndAlso Not IsNothing(TargetRight.Parent) Then TargetRight.SetValue(ContentPresenter.ContentProperty, Nothing)
        PageLeft = TargetLeft
        PageRight = TargetRight
        '触发页面通用动画
        CType(PanMainLeft.Child, MyPageLeft).TriggerHideAnimation()
        CType(PanMainRight.Child, MyPageRight).PageOnExit()
        AniControlEnabled -= 1
        '执行动画
        AniStart({
            AaCode(
            Sub()
                AniControlEnabled += 1
                '把新页面添加进容器
                PanMainLeft.Child = PageLeft
                PageLeft.Opacity = 0
                PanMainLeft.Background = Nothing
                AniControlEnabled -= 1
                RunInUi(Sub() PanMainLeft_Resize(PanMainLeft.ActualWidth), True)
            End Sub, 110),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                PageLeft.Opacity = 1
                PageLeft.TriggerShowAnimation()
            End Sub, 30, True)
        }, "FrmMain PageChangeLeft")
        AniStart({
            AaCode(
            Sub()
                AniControlEnabled += 1
                CType(PanMainRight.Child, MyPageRight).PageOnForceExit()
                '把新页面添加进容器
                PanMainRight.Child = PageRight
                PageRight.Opacity = 0
                PanMainRight.Background = Nothing
                AniControlEnabled -= 1
                RunInUi(Sub() BtnExtraBack.ShowRefresh(), True)
            End Sub, 110),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                PageRight.Opacity = 1
                PageRight.PageOnEnter()
            End Sub, 30, True)
        }, "FrmMain PageChangeRight")
    End Sub
    ''' <summary>
    ''' 退出子界面。
    ''' </summary>
    Private Sub PageChangeExit()
        If PageStack.Any Then
            '子页面 → 主页面，退出
            PanTitleMain.Visibility = Visibility.Visible
            PanTitleMain.IsHitTestVisible = True
            PanTitleInner.IsHitTestVisible = False
            AniStart({
                AaOpacity(PanTitleInner, -PanTitleInner.Opacity, 150),
                AaX(PanTitleInner, -18 - PanTitleInner.Margin.Left, 150,, New AniEaseInFluent),
                AaOpacity(PanTitleMain, 1 - PanTitleMain.Opacity, 150, 200),
                AaX(PanTitleMain, -PanTitleMain.Margin.Left, 350, 200, New AniEaseOutBack(AniEasePower.Weak)),
                AaCode(Sub() PanTitleInner.Visibility = Visibility.Collapsed,, True)
            }, "FrmMain Titlebar FirstLayer")
            PageStack.Clear()
        Else
            '主页面 → 主页面，无事发生
        End If
    End Sub

    '左边栏改变
    Private Sub PanMainLeft_SizeChanged(sender As Object, e As SizeChangedEventArgs) Handles PanMainLeft.SizeChanged
        If Not e.WidthChanged Then Return
        PanMainLeft_Resize(e.NewSize.Width)
    End Sub
    Private Sub PanMainLeft_Resize(NewWidth As Double)
        Dim Delta As Double = NewWidth - RectLeftBackground.Width
        If Math.Abs(Delta) > 0.1 AndAlso AniControlEnabled = 0 Then
            If PanMain.Opacity < 0.1 Then PanMainLeft.IsHitTestVisible = False '避免左边栏指向背景未能完美覆盖左边栏
            If NewWidth > 0 Then
                '宽度足够，显示
                AniStart({
                    AaWidth(RectLeftBackground, NewWidth - RectLeftBackground.Width, 180,, New AniEaseOutFluent(AniEasePower.ExtraStrong)),
                    AaOpacity(RectLeftShadow, 1 - RectLeftShadow.Opacity, 180),
                    AaCode(Sub() PanMainLeft.IsHitTestVisible = True, 150)
                }, "FrmMain LeftChange", True)
            Else
                '宽度不足，隐藏
                AniStart({
                    AaWidth(RectLeftBackground, -RectLeftBackground.Width, 180,, New AniEaseOutFluent),
                    AaOpacity(RectLeftShadow, -RectLeftShadow.Opacity, 180),
                    AaCode(Sub() PanMainLeft.IsHitTestVisible = True, 150)
                }, "FrmMain LeftChange", True)
            End If
        Else
            RectLeftBackground.Width = NewWidth
            PanMainLeft.IsHitTestVisible = True
            AniStop("FrmMain LeftChange")
        End If
    End Sub

#End Region

#Region "控件拖动"

    '在时钟中调用，使得即使鼠标在窗口外松开，也可以释放控件
    Public Sub DragTick()
        If DragControl Is Nothing Then Return
        If Not Mouse.LeftButton = MouseButtonState.Pressed Then
            DragStop()
        End If
    End Sub
    '在鼠标移动时调用，以改变 Slider 位置
    Public Sub DragDoing() Handles PanBack.MouseMove
        If DragControl Is Nothing Then Return
        If Mouse.LeftButton = MouseButtonState.Pressed Then
            DragControl.DragDoing()
        Else
            DragStop()
        End If
    End Sub
    Public Sub DragStop()
        '存在其他线程调用的可能性，因此需要确保在 UI 线程运行
        RunInUi(Sub()
                    If DragControl Is Nothing Then Return
                    Dim Control = DragControl
                    DragControl = Nothing
                    Control.DragStop() '控件会在该事件中判断 DragControl，所以得放在后面
                End Sub)
    End Sub

#End Region

#Region "附加按钮"

    '音乐
    Private Sub BtnExtraMusic_Click(sender As Object, e As EventArgs) Handles BtnExtraMusic.Click
        MusicControlPause()
    End Sub
    Private Sub BtnExtraMusic_RightClick(sender As Object, e As EventArgs) Handles BtnExtraMusic.RightClick
        MusicControlNext()
    End Sub

    '下载管理
    Private Sub BtnExtraDownload_Click(sender As Object, e As EventArgs) Handles BtnExtraDownload.Click
        PageChange(PageType.DownloadManager)
    End Sub
    Private Function BtnExtraDownload_ShowCheck() As Boolean
        Return HasDownloadingTask() AndAlso Not PageCurrent = PageType.DownloadManager
    End Function

    '投降
    Public Sub AprilGiveup() Handles BtnExtraApril.Click
        If IsAprilEnabled AndAlso Not IsAprilGiveup Then
            Hint("=D", HintType.Green)
            IsAprilGiveup = True
            FrmLaunchLeft.AprilScaleTrans.ScaleX = 1
            FrmLaunchLeft.AprilScaleTrans.ScaleY = 1
            BtnExtraApril.ShowRefresh()
        End If
    End Sub
    Public Function BtnExtraApril_ShowCheck() As Boolean
        Return IsAprilEnabled AndAlso Not IsAprilGiveup AndAlso PageCurrent = PageType.Launch
    End Function

    '关闭 Minecraft
    Public Sub BtnExtraShutdown_Click() Handles BtnExtraShutdown.Click
        Try
            If McLaunchLoaderReal IsNot Nothing Then McLaunchLoaderReal.Cancel()
            For Each Watcher In McWatcherList
                Watcher.Kill()
            Next
            Hint("已关闭运行中的 Minecraft！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "强制关闭所有 Minecraft 失败")
        End Try
    End Sub
    Public Function BtnExtraShutdown_ShowCheck() As Boolean
        Return HasRunningMinecraft
    End Function

    ''' <summary>
    ''' 返回顶部。
    ''' </summary>
    Public Sub BackToTop() Handles BtnExtraBack.Click
        Dim RealScroll As MyScrollViewer = BtnExtraBack_GetRealChild()
        If RealScroll IsNot Nothing Then
            RealScroll.PerformVerticalOffsetDelta(-RealScroll.VerticalOffset)
        Else
            Logger.Error("无法返回顶部，未找到合适的 RealScroll", LogBehavior.Toast)
        End If
    End Sub
    Private Function BtnExtraBack_ShowCheck() As Boolean
        Dim RealScroll As MyScrollViewer = BtnExtraBack_GetRealChild()
        Return RealScroll IsNot Nothing AndAlso RealScroll.Visibility = Visibility.Visible AndAlso RealScroll.VerticalOffset > Height + If(BtnExtraBack.Show, 0, 700)
    End Function
    Private Function BtnExtraBack_GetRealChild() As MyScrollViewer
        If PanMainRight.Child Is Nothing OrElse TypeOf PanMainRight.Child IsNot MyPageRight Then Return Nothing
        Dim Page As MyPageRight = PanMainRight.Child
        Return Page.FindName(Page.PanScroll)
    End Function

#End Region

    '愚人节鼠标位置
    Public lastMouseArg As MouseEventArgs = Nothing
    Private Sub FormMain_MouseMove(sender As Object, e As MouseEventArgs) Handles Me.MouseMove
        lastMouseArg = e
    End Sub

End Class
