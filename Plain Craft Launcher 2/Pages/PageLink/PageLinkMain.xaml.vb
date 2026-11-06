Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Net.Sockets
Imports System.Reflection
Imports PCL

Public Class PageLinkMain
    '邀请码格式版本。3 = 房间名带 NET_PREFIX 前缀（配合中继白名单）
    'EasyTier 联机模块压缩包的 SHA256（按架构）。
    '这个包从 GitHub 下载、解压后以管理员权限运行，不校验哈希等于把
    '「远程代码执行」交给任何能拦截该下载的人（恶意根证书、被控 CDN 等）。
    '⚠️ 取值只能用 GitHub Releases API 的 assets[].digest（权威值）。
    '   千万不要「下载一份然后算哈希」——网络不稳时会拿到截断文件，
    '   算出来的哈希是错的，会把正常用户全部拦下（这个坑已经踩过一次：
    '   当时是在 7.9 MB 的截断包上算的，真实大小 32.6 MB）。
    '   API: https://api.github.com/repos/EasyTier/EasyTier/releases/tags/v2.6.4
    Private Const EasytierZipSha256X64 As String =
        "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84"
    '官方大小：哈希比对前先比大小，把「下载不完整」和「被篡改」区分开
    Private Const EasytierZipSizeX64 As Integer = 32657720

    ''' <summary>返回当前架构对应的联机模块哈希，未知架构返回空字符串。</summary>
    Private Shared Function ExpectedEasytierZipSha256(Arch As String) As String
        Select Case Arch
            Case "x86_64" : Return EasytierZipSha256X64
            Case Else : Return ""
        End Select
    End Function

    ''' <summary>官方压缩包大小；未知架构返回 0。</summary>
    Private Shared Function ExpectedEasytierZipSize(Arch As String) As Integer
        If Arch = "x86_64" Then Return EasytierZipSizeX64
        Return 0
    End Function

    Private Const INVITE_CODE_VERSION As Integer = 3

''' <summary>
''' 房间名前缀。**这个值和中继的 --relay-network-whitelist 是一对**：
    ''' 中继白名单目前放行 PCLB-* 与 MITCB-* 两种前缀，见 NET_PREFIX 说明。
''' 要改前缀，必须先在两个中继上改白名单并重启，再发客户端。
''' </summary>
    Private Const NET_PREFIX As String = "MITCB-"
    '房间名 = {前缀}P{4位端口hex}-{5位随机}
    '  前缀长度 + 11：PCLB-（5）→ 16 字符，MITCB-（6）→ 17 字符
    Private Shared ReadOnly CODE_NAME_LEN As Integer = NET_PREFIX.Length + 11
    '邀请码 = 房间名 + '-' + 5位密码 + '-' + 2位版本 + 3位发现节点 = 房间名长度 + 12
    Private Shared ReadOnly CODE_TOTAL_LEN As Integer = CODE_NAME_LEN + 12
    '===============================
    '  状态机与前端页面
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"主状态由 {OldState} 变更为 {NewState}")
        '触发状态切换
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    '让加载器的 成功/失败 事件触发状态切换
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    Telemetry("联机失败", "Exception", FilterUserName(Loader.Error.GetDisplay(True), "*"))
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    '页面动画兼容
    ''' <summary>
    ''' 进入联机页面时的前置说明：点「继续」才申请管理员权限，点「算了」什么也不做。
    ''' </summary>
    Private Shared AdminNoticeShown As Boolean = False
    ''' <summary>
    ''' 进入联机页时给一次提醒：启动联机时 Windows 会请求管理员权限。
    ''' 这里不再重启整个启动器——重启拿不到管理员令牌时会导致弹窗循环（见 482 的排查）。
    ''' </summary>
    Private Sub CheckAdminOnEnter() Handles Me.PageEnter
        If AdminNoticeShown Then Return
        AdminNoticeShown = True
        If WindowsUtils.HasAdminRole() Then Return
        Logger.Info("当前非管理员：启动联机时只对联机模块申请一次提权")
    End Sub

    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        FrmLinkMain.PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
        FrmLinkMain.PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
    End Sub

#End Region

#Region "初始页面 (Waiting)"

    '由任意状态切换到 Waiting
    Private Sub SwitchToWaiting(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock
        ProcessStop()
        '页面切换
        If OldState <> LinkStates.Finished Then
            '从完成页面退出时，PageOnContentExit 结束会触发 PageEnter 事件，这会调用 UpdatePanelVisibility
            '如果在现在提前调用了，会丢失完成页面的退出动画
            UpdatePanelVisibility()
        End If
        PageOnContentExit()
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '创建
    Private Sub Create_MouseLeftButtonUp() Handles PanSelectCreate.MouseLeftButtonUp
        '输入端口号
        Dim Port As String = MyMsgBoxInput("输入端口", $"在单人游戏的暂停菜单选择 {vbLQ}对局域网开放{vbRQ}，然后输入端口数字。{vbCrLf}甚至可以输入其他游戏的端口……嗯……",
            ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
            HintText:="端口号")
        If Port Is Nothing Then Return
        '基础信息
        IsServerSide = True
        ServerPort = Port
        Dim GenerateRandomCode =
        Function() As String
            Dim Result As String = ""
            For i = 1 To 5
                Result &= RandomInteger(0, 35).ConvertRadix(36)
            Next
            Return Result.Replace("O", "0").Replace("I", "1")
        End Function
        '前缀是给中继白名单认的：中继只转发白名单里的前缀，
        '别人随便起的名字用不了我们的中继。
        '房间名长度 = 前缀长度 + 11：MITCB-（6）→ 17 字符，PCLB-（5）→ 16 字符。
        NetworkName = $"{NET_PREFIX}P{ServerPort.ConvertRadix(16).PadLeft(4, "0"c)}-{GenerateRandomCode()}"
        NetworkSecret = GenerateRandomCode()
        DiscoverNodeID = -1
        Logger.Info($"尝试创建房间，网络名 {NetworkName}，网络密码 {NetworkSecret}，端口 {ServerPort}")
        '启动
        ChangeState(LinkStates.Loading)
    End Sub

    '加入
    Private Shared LastCode As String = Nothing
    ''' <summary>
    ''' 输入邀请码，切换到联机页并立即加入房间。
    ''' </summary>
    Public Shared Sub Join() Handles PanSelectJoin.MouseLeftButtonUp        '授权已被后台判定失效时不允许联机
        If Not LicenseGateForAction() Then Return
        Dim Code As String = MyMsgBoxInput("输入邀请码", "输入房主发给你的邀请码。",
            HintText:=If(String.IsNullOrEmpty(LastCode), "", "使用上一次的邀请码"))
        If Not String.IsNullOrEmpty(LastCode) AndAlso Code IsNot Nothing AndAlso Code = "" Then Code = LastCode
        If Code Is Nothing Then Return
        Join(Code)
    End Sub
    Public Sub JoinInternal(Code As String)
        If Code Is Nothing Then Return
        '基础格式校验
        Dim ValidateResult = ValidateCodeFormat(Code)
        If ValidateResult IsNot Nothing Then
            Hint(ValidateResult, HintType.Red)
            Return
        End If
        Code = FixCodeFormat(Code)
        Logger.Info($"实际使用的邀请码：{Code}")
        '基础信息
        IsServerSide = False
        '新格式：{NET_PREFIX}P{4hex}-{5rand}-{5secret}-{2ver}{3disc}
        '        [0..16)                     名称
        '                [17..22)            密码
        '                        [23..25)    版本
        '                                [25..28) 发现节点
        '偏移全部相对前缀：{前缀}P{4hex}-{5rand}-{5secret}-{2ver}{3disc}
        ServerPort = Code.Substring(NET_PREFIX.Length + 1, 4).ConvertRadix(16, 10)
        NetworkName = Code.Substring(0, CODE_NAME_LEN)
        NetworkSecret = Code.Substring(CODE_NAME_LEN + 1, 5)
        If Code.Substring(CODE_NAME_LEN + 9, 3) = "000" Then
            DiscoverNodeID = -2
        Else
            DiscoverNodeID = Code.Substring(CODE_NAME_LEN + 9, 3).ConvertRadix(16, 10)
        End If
        Logger.Info($"尝试加入房间，网络名 {NetworkName}，网络密码 {NetworkSecret}，端口 {ServerPort}，发现节点 {DiscoverNodeID}")
        '启动
        LastCode = Code
        ChangeState(LinkStates.Loading)
    End Sub
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If Code Is Nothing Then Return "邀请码为空！"
        Code = FixCodeFormat(Code)
        '判断类型
        If Not (Code.Length = CODE_TOTAL_LEN AndAlso Code.StartsWithF(NET_PREFIX) AndAlso
                Code(NET_PREFIX.Length - 1) = "-"c AndAlso Code(NET_PREFIX.Length + 5) = "-"c AndAlso
                Code(CODE_NAME_LEN) = "-"c AndAlso Code(CODE_NAME_LEN + 6) = "-"c) Then
            If Code.StartsWithF("U/") Then 'HMCL
                Return "请让房主使用 PCL MITC 创建房间！"
            ElseIf Code.Length = 10 Then 'PCL CE
                Return "请让房主使用非社区版的 PCL MITC 创建房间！"
            ElseIf Code.Length = 23 AndAlso Code(0) = "P"c AndAlso Code(11) = "-"c Then
                '旧格式（没有前缀）
                Return $"这是旧版邀请码。请双方都升级到最新版 PCL MITC 后再联机——新版房间名带 {NET_PREFIX} 前缀，中继只给这种房间转发。"
            Else
                Return "邀请码有误，请让房主使用 PCL MITC 创建房间！"
            End If
        End If
        '校验版本
        If Val(Code.Substring(CODE_NAME_LEN + 7, 2)) > INVITE_CODE_VERSION Then Return "你的 PCL MITC 版本太老了，请在更新 PCL MITC 之后再联机！"
        Return Nothing
    End Function
    Private Shared Function FixCodeFormat(Code As String) As String
        Code = Code.Between("【", "】").Between("[", "]") '从完整消息中提取
        Code = Code.Upper.Replace("O", "0").Replace("I", "1") '输入修正
        '旧码兼容：只处理历史格式 17 字符的 "PCLB-Pxxxx-yyyyy"（无密码与版本段）
        If Code.StartsWithF("PCLB-") AndAlso Code.Length = 17 Then
            Code = Code & "-0105E"
        End If

        Return Code
    End Function

    '自动加入

    ''' <summary>
    ''' 切换到联机页并立即加入指定房间。
    ''' </summary>
    Public Shared Sub Join(Code As String)        '授权已被后台判定失效时不允许联机
        If Not LicenseGateForAction() Then Return
        If LinkState <> LinkStates.Waiting Then
            Hint("你已经在联机房间中了！", HintType.Red)
        ElseIf FrmMain.PageCurrent = FormMain.PageType.Link Then
            FrmLinkMain.JoinInternal(Code)
        Else
            AutoJoinCode = Code
            FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
        End If
    End Sub
    '在进入页面时自动尝试加入房间
    Private Shared AutoJoinCode As String = Nothing
    Private Sub PanSelectJoin_Loaded() Handles PanSelectJoin.Loaded
        If AutoJoinCode Is Nothing Then Return
        If LinkState = LinkStates.Waiting Then JoinInternal(AutoJoinCode)
        AutoJoinCode = Nothing
    End Sub

#End Region

#Region "加载页面 (Loading | Failed)"

    '由 Waiting 或 Failed 状态切换到 Loading
    Private Sub SwitchToLoading(OldState As LinkStates)
        '清理变量
        Peers = Nothing
        FailCount = 0
        '加载器与进程状态
        LinkLoader.Start(IsForceRestart:=True)
        '页面切换会由 Loader 调用 MyPageRight 来触发
        'UI 更新
        UpdateProgressBar(0)
        LabLoadTitle.Text = If(IsServerSide, "创建房间中", "加入房间中")
        UpdateLoadingPage("正在初始化……", "准备初始化")
    End Sub

    '由 Loading 状态切换到 Failed
    Private Sub SwitchToFailed(OldState As LinkStates)
        '加载器与进程状态
        LinkLoader.Cancel()
        ProcessStop()
        'UI 更新
        UpdateProgressBar(1)
        '显示错误信息
        LabLoadTitle.Text = FailReason
        Dim Brief As String = LinkLoader.Error.GetDisplay(False)
        Dim InnerEx As Exception = LinkLoader.Error
        If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Do Until InnerEx.InnerException Is Nothing
            InnerEx = InnerEx.InnerException
            If InnerEx.Message.StartsWithF("$") Then Brief = InnerEx.Message
        Loop
        LabLoadDesc.Text = If(Brief.StartsWithF("$"), Brief.TrimStart("$"), LinkLoader.Error.GetDisplay(True))
        Logger.Warn(LinkLoader.Error, LabLoadTitle.Text)
    End Sub

    '===============================
    '  页面 UI
    '===============================

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        If FailReason = FailBrief & "失败" Then Return
        FailReason = FailBrief & "失败"
        Logger.Info($"开始步骤：{Title}")
        RunInUiWait(
        Sub()
            If FrmLinkMain Is Nothing OrElse Not FrmLinkMain.LabLoadDesc.IsLoaded Then Return
            FrmLinkMain.LabLoadDesc.Text = Title
        End Sub)
    End Sub
    Private FailReason As String = "准备初始化"

    '取消加载
    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        ChangeState(LinkStates.Waiting)
    End Sub

    '点击重试
    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        ChangeState(LinkStates.Loading)
    End Sub

    '进度条
    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress +
                (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub
    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    '由 Loading 状态切换到 Finished
    Private Sub SwitchToFinished(OldState As LinkStates)
        '由于只可能从加载器的完成事件触发，不需要管加载器
        UpdatePanelVisibility() '页面的实际切换会由 Loader 调用 MyPageRight 来触发

        'UI 更新
        If IsServerSide Then
            LabFinishTitle.Text = "已创建房间"
            LabFinishDesc.Text = $"把邀请码发给朋友，让大家加入房间吧！{vbCrLf}邀请码：{GetInviteCode()}"
            BtnFinishExit.Text = "关闭"
            BtnFinishPing.ToolTip = "网络延迟"
            BtnFinishCopy.Visibility = Visibility.Visible
            Copy() '立即复制邀请码
            '下边栏
            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            LabFinishPort.Text = ServerPort
        Else
            LabFinishTitle.Text = "已加入房间"
            LabFinishDesc.Text = $"在多人游戏页面的最下方就能找到联机房间！{vbCrLf}注意：使用离线登录时不要手动输入 IP！"
            BtnFinishExit.Text = "离开"
            BtnFinishPing.ToolTip = "与房主的延迟"
            BtnFinishCopy.Visibility = Visibility.Collapsed
            '下边栏
            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            LabFinishIp.Text = ClientAddress
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
        End If
        Update() '立即刷新
    End Sub

    '===============================
    '  页面 UI
    '===============================

    '退出
    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub
    ''' <summary>
    ''' 退出联机。
    ''' 返回是否弹出了警告窗口并且玩家选择了取消。
    ''' </summary>
    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent Then
            If IsServerSide AndAlso PeopleCount > 1 Then
                If MyMsgBox("你确定要关闭联机房间吗？" & vbCrLf & "所有玩家都需要重新输入邀请码才可加入游戏！", "退出联机", "确定", "取消", IsWarn:=True) = 2 Then Return True
            ElseIf Closing Then
                If MyMsgBox(If(IsServerSide, "你确定要关闭联机房间吗？", "你确定要离开联机房间吗？"), "退出联机", "确定", "取消") = 2 Then Return True
            End If
        End If
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    '复制邀请码
    Private Sub Copy() Handles BtnFinishCopy.Click
        Dim CodeText As String = $"在 PCL MITC 启动器中输入邀请码【{GetInviteCode()}】，即可加入联机房间！"
        ClipboardSet(CodeText, False)
        Settings.Set("LinkLastAutoJoinInviteCode", CodeText)
        Hint("已复制邀请码！", HintType.Green)
    End Sub
    Private Function GetInviteCode() As String
        Return $"{NetworkName}-{NetworkSecret}-{INVITE_CODE_VERSION.ToString.PadLeft(2, "0"c)}{ _
            If(DiscoverNodeID = -1, 0, DiscoverNodeID).ConvertRadix(16).PadLeft(3, "0"c)}"
    End Function

    '复制 IP
    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        ClipboardSet(ClientAddress, False)
        Hint("已复制服务器地址！", HintType.Green)
    End Sub

#End Region

    '===============================
    '  后端
    '===============================

    ''' <summary>
    ''' EasyTier 所在的文件夹路径，以 \ 结尾。
    ''' </summary>
    Private PathEasyTier As String = Paths.AppDataThenName & "EasyTier\"
    ''' <summary>
    ''' 联机模块实际运行的目录。
    ''' 正常情况下就是 PathEasyTier；如果上次解压时原目录里的文件被占用
    '''（典型是 WinDivert64.sys 被别的程序加载过），会换成独立子目录，
    ''' 这里优先用那个目录，避免和锁死的旧文件继续纠缠。
    ''' </summary>
    Private Function ModuleDir() As String
        Try
            Dim Override As String = EasyTierRunDirOverride
            If Not String.IsNullOrWhiteSpace(Override) AndAlso
               FileUtils.Exists(Override & "联机模块.exe") AndAlso
               FileUtils.Exists(Override & "联机模块 CLI.exe") Then
                Return Override
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取联机模块备用目录失败")
        End Try
        Return PathEasyTier
    End Function
    ''' <summary>
    ''' 当前是服务端还是客户端。
    ''' </summary>
    Private IsServerSide As Boolean
    ''' <summary>
    ''' 服务端映射前的端口号。
    ''' </summary>
    Private ServerPort As Integer
    ''' <summary>
    ''' 客户端映射后的端口号。
    ''' </summary>
    Private ClientPort As Integer
    ''' <summary>
    ''' RPC 端口号。
    ''' </summary>
    Private RPCPort As Integer
    ''' <summary>
    ''' 在客户端映射后的地址。
    ''' 除非已作为客户端启动 EasyTier，否则一直为 Nothing。
    ''' </summary>
    Private ClientAddress As String = Nothing
    ''' <summary>
    ''' 发现节点的 ID。
    ''' 若必须设定自定义节点则为 -2；若等待选择则为 -1；选择回退节点则为 0；否则为对应节点的 ID。
    ''' </summary>
    Private DiscoverNodeID As Integer = -1
    ''' <summary>
    ''' 网络信息。
    ''' </summary>
    Private NetworkName As String, NetworkSecret As String

#Region "加载"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机", {
        New LoaderTask(Of Integer, Integer)("获取配置", AddressOf InitConfig) With {.ProgressWeight = 8},
        New LoaderTask(Of Integer, List(Of NetFile))("准备下载联机模块", AddressOf InitPrepareDownload) With {.ProgressWeight = 2},
        New LoaderDownload("下载联机模块", New List(Of NetFile)) With {.ProgressWeight = 40},
        New LoaderTask(Of Integer, Integer)("启动联机模块", AddressOf InitLaunch) With {.ProgressWeight = 50}
    })

    '1. 获取服务器配置
    Private Sub InitConfig(Task As LoaderTask(Of Integer, Integer))
        '主程序本身不需要管理员权限：提权只发生在启动联机模块那一步（见 ProcessStart）。
        '所以这里不再拦截非管理员。

        UpdateLoadingPage("正在联网获取配置……", "联网获取配置")
        '本修改版在 ModSecret.RefreshServerConfig 中自行填充 ServerConfig，
        '所以开源分支同样可以继续联机流程，不再因为分支名而直接拒绝。
        If ServerConfig Is Nothing Then RefreshServerConfig()
        ServerLoader.WaitForExit(LoaderToSyncProgress:=Task)
        If ServerConfig Is Nothing Then RefreshServerConfig()
        If ServerConfig Is Nothing Then
            Throw New Exception($"$未能获取联机配置。{vbCrLf}本修改版不联网拉取该配置，请向维护者反馈。")
        End If
        '检查是否已禁用联机功能
        Dim DisableReason = ServerConfig("Link")?("DisableReason2")?.ToString
        If Not String.IsNullOrEmpty(DisableReason) Then Throw New Exception("$" & DisableReason) 'TODO: 可能不会显示错误原因？
        If CType(ServerConfig("Link"), JObject).ContainsKey("MinVersionCode") AndAlso
           VersionCode < ServerConfig("Link")("MinVersionCode").ToObject(Of Integer) Then
            Throw New Exception("$你的 PCL MITC 版本太老了，请在更新 PCL MITC 之后再联机！")
        End If
        '提前校验中继节点：避免用户先白等约 30 MB 的联机模块下载，
        '下载完才被告知必须去填节点地址。
        '注意：内置节点在 Nodes 里（带到期时间），Peers 只是旧的兜底字段。
        If String.IsNullOrWhiteSpace(Settings.Get(Of String)("LinkCustomPeer")) AndAlso
           LinkNodePeers().Count = 0 AndAlso
           ServerConfig("Link")("Peers").Count() = 0 Then
            Throw New Exception(
                $"$所有内置联机节点都已到期，你也没有填写自定义节点。{vbCrLf}{vbCrLf}" &
                $"请打开 {vbLQ}设置 → 联机 → 自定义节点{vbRQ} 填写中继地址。{vbCrLf}" &
                $"格式如 tcp://节点地址:11010 或 udp://节点地址:11010。{vbCrLf}{vbCrLf}" &
                $"房主与所有加入者必须填写至少一个相同的节点。")
        End If
    End Sub

    '2. 获取需要下载的文件
    Private ServerVersion As Integer
    Private Sub InitPrepareDownload(Task As LoaderTask(Of Integer, List(Of NetFile)))
        '获取 CPU 架构
        UpdateLoadingPage("正在获取 CPU 架构……", "获取 CPU 架构")
        Dim Architecture As String = GetType(String).Assembly.GetName().ProcessorArchitecture
        Select Case Architecture
            Case ProcessorArchitecture.X86
                Architecture = "i686"
            Case ProcessorArchitecture.Amd64
                Architecture = "x86_64"
            Case ProcessorArchitecture.Arm
                Architecture = "arm64"
            Case Else
                Logger.Warn($"CPU 是不支持的 {Architecture} 架构，这可能会导致联机模块无法启动！")
                Architecture = "arm64"
        End Select
        Logger.Info($"CPU 架构：{Architecture}")
        Telemetry("联机开始")
        '检查 EasyTier 版本
        UpdateLoadingPage("正在检查联机模块版本……", "检查联机模块版本")
        If Not (FileUtils.Exists(ModuleDir() & "联机模块 CLI.exe") AndAlso FileUtils.Exists(ModuleDir() & "联机模块.exe") AndAlso
                FileUtils.Exists(ModuleDir() & "Packet.dll")) Then
            Settings.Set("LinkEasyTierVersion", -1)
        End If
        Dim LocalVersion As Integer = Settings.Get(Of Integer)("LinkEasyTierVersion")
        '优先用服务端下发的模块版本（签名保护），没有就用编译期内置的
        Dim ModuleVer As Integer = LinkModuleVersion()
        ServerVersion = If(ModuleVer > 0, ModuleVer, ServerConfig("Link")("EasyTierVersion").ToObject(Of Integer)())
        Dim RequiredFiles As New List(Of NetFile)
        Logger.Info($"EasyTier 本地版本：{LocalVersion}，需求版本：{ServerVersion}")
        If LocalVersion < ServerVersion Then
            '下载地址优先取签名清单里的，没有才用内置列表
            Dim ModuleEntry As JObject = LinkModuleEntry(Architecture)
            Dim ModuleUrl As String = If(ModuleEntry IsNot Nothing,
                If(ModuleEntry("url")?.ToString(), "").Trim, "")
            Dim UrlList As New List(Of String)
            If ModuleUrl.Length > 0 Then UrlList.Add(ModuleUrl)
            '内置地址始终作为兜底：我们自己的镜像挂了就自动退回官方源，反之亦然。
            '两个域名不同，属于真正的冗余，不是同源重复。
            For Each UrlEntry In ServerConfig("Link")("Downloads")
                Dim FallbackUrl As String = UrlEntry.ToString().Replace("{arch}", Architecture).Trim
                If FallbackUrl.Length > 0 AndAlso
                   Not UrlList.Any(Function(U) U.Equals(FallbackUrl, StringComparison.OrdinalIgnoreCase)) Then
                    UrlList.Add(FallbackUrl)
                End If
            Next
            RequiredFiles.Add(New NetFile(
                UrlList,
                PathEasyTier & "EasyTier.zip",
                New FileChecker With {.MinSize = 1024 * 1024 * 2}))
        End If
        '开始下载
        UpdateLoadingPage("正在下载联机模块……", "下载联机模块")
        Task.Output = RequiredFiles
    End Sub

    '3. 启动联机模块
    Private Sub InitLaunch(Task As LoaderTask(Of Integer, Integer))
        '先结束可能残留的联机模块：它加载着 WinDivert64.sys，
        '文件被占用时覆盖会失败，表现为「解压联机模块失败 / 正由另一进程使用」。
        ProcessStop()
        '解压文件
        UpdateLoadingPage("正在解压联机模块……", "解压联机模块")
        If FileUtils.Exists(PathEasyTier & "EasyTier.zip") Then
            '先校验哈希，通过了才解压。解压出来的 exe 会以管理员权限启动，
            '所以这一步是「远程代码执行」的唯一闸门。
            Dim ZipArch As String = "x86_64"
            Select Case GetType(String).Assembly.GetName().ProcessorArchitecture
                Case ProcessorArchitecture.X86 : ZipArch = "i686"
                Case ProcessorArchitecture.Arm : ZipArch = "arm64"
            End Select
            '期望哈希：签名清单优先，其次编译期内置常量
            Dim ExpectedSha As String = ""
            Dim HasManifestHash As Boolean = False
            Dim HashEntry As JObject = LinkModuleEntry(ZipArch)
            If HashEntry IsNot Nothing Then
                ExpectedSha = If(HashEntry("sha256")?.ToString(), "").Trim().ToLower()
                HasManifestHash = ExpectedSha.Length > 0
            End If
            If ExpectedSha = "" Then ExpectedSha = ExpectedEasytierZipSha256(ZipArch)
            If ExpectedSha = "" Then
                Logger.Warn($"当前架构 {ZipArch} 没有预置联机模块哈希，跳过校验（存在被篡改风险）")
                Hint("联机模块未做哈希校验，请确认网络环境可信", HintType.Red, False)
            Else
                '先比大小：下载被截断时报「不完整」而不是「被篡改」，
                '否则用户会以为是网络被劫持，实际只是下载中断。
                '只在用编译期内置常量时才比：内置大小只对应内置的那一个版本，
                '服务端升级 EasyTier 后大小会变，而清单里的 sha256 才是权威依据。
                Dim ExpectedSize As Integer = 0
                If Not HasManifestHash Then ExpectedSize = ExpectedEasytierZipSize(ZipArch)
                Dim ActualSize As Long = New IO.FileInfo(PathEasyTier & "EasyTier.zip").Length
                If ExpectedSize > 0 AndAlso ActualSize <> ExpectedSize Then
                    Logger.Error($"联机模块大小不符：期望 {ExpectedSize}，实际 {ActualSize}")
                    FileUtils.Delete(PathEasyTier & "EasyTier.zip")
                    Settings.Set("LinkEasyTierVersion", -1)
                    Panic("联机模块下载不完整",
                        $"下载到的联机模块只有 {ActualSize:N0} 字节，应为 {ExpectedSize:N0} 字节。{vbCrLf}{vbCrLf}" &
                        $"已删除该文件，请重试；网络不稳定时可以多试几次。")
                    Return
                End If
                Dim ActualSha As String = CryptographyUtils.ComputeFileHash(
                    PathEasyTier & "EasyTier.zip", CryptographyUtils.HashMethod.Sha256).Trim().ToLower()
                If ActualSha <> ExpectedSha Then
                    Logger.Error($"联机模块校验失败：期望 {ExpectedSha}，实际 {ActualSha}")
                    FileUtils.Delete(PathEasyTier & "EasyTier.zip")
                    Settings.Set("LinkEasyTierVersion", -1)
                    Panic("联机模块校验失败",
                        $"下载的联机模块与预期哈希不一致，可能被中途篡改。{vbCrLf}{vbCrLf}" &
                        $"已删除该文件，请重试；若反复失败，说明你的网络或代理可能被劫持。")
                    Return
                End If
                Logger.Info("联机模块哈希校验通过")
            End If
            '解压
            Dim ExtractPath As String = RequestTaskTempFolder()
            FileUtils.ExtractToDirectory(PathEasyTier & "EasyTier.zip", ExtractPath,
                progressHandler:=Sub(Percentage) Task.Progress = Percentage * 0.05)
            '覆盖前再杀一遍：进程刚被杀掉时系统可能还没完全释放 WinDivert 驱动，
            '所以这里带重试，6 次 × 400ms 足够驱动卸载完成。
            Dim SourceDir As String = DirectoryUtils.EnumerateDirectories(ExtractPath, True).First
            Dim CopyDone As Boolean = False
            Dim LastError As Exception = Nothing
            For Attempt As Integer = 1 To 6
                Try
                    DirectoryUtils.Copy(SourceDir, PathEasyTier)
                    CopyDone = True
                    Exit For
                Catch ex As IO.IOException
                    LastError = ex
                    Logger.Warn(ex, $"解压联机模块第 {Attempt}/6 次失败，文件可能仍被占用，稍后重试")
                    ProcessStop()
                    Thread.Sleep(400)
                Catch ex As UnauthorizedAccessException
                    LastError = ex
                    Logger.Warn(ex, $"解压联机模块第 {Attempt}/6 次失败（权限不足），稍后重试")
                    ProcessStop()
                    Thread.Sleep(400)
                End Try
            Next
            '覆盖失败最可能的原因是目标目录里有文件被占用：WinDivert64.sys 被某个程序
            '加载后，驱动卸载前这个文件会一直被锁着，杀进程也未必立刻释放。
            '这时不再和旧文件较劲，换到 alt-<版本> 独立子目录解压，运行时也指向那里。
            Dim TargetDir As String = PathEasyTier
            If Not CopyDone Then
                Dim AltDir As String = PathEasyTier & "alt-" & ServerVersion & "\"
                Try
                    IO.Directory.CreateDirectory(AltDir)
                    DirectoryUtils.Copy(SourceDir, AltDir)
                    TargetDir = AltDir
                    EasyTierRunDirOverride = AltDir
                    Logger.Info($"联机模块原目录被占用，已改用备用目录：{AltDir}")
                    CopyDone = True
                Catch ex As Exception
                    LastError = ex
                End Try
            End If
            If Not CopyDone Then
                Logger.Error($"解压联机模块失败：{LastError?.Message}")
                Panic("解压联机模块失败",
                    $"无法把联机模块写入 {vbLQ}{PathEasyTier}{vbRQ}，里面的文件正被其他程序占用。{vbCrLf}{vbCrLf}" &
                    $"最常见的原因是上一次的 {vbLQ}联机模块.exe{vbCrLf}联机模块 CLI.exe{vbRQ} 没有正常退出。请按顺序试：" & vbCrLf &
                    $"1. 打开任务管理器，结束所有 {vbLQ}联机模块{vbRQ} 开头的进程；" & vbCrLf &
                    $"2. 回到本页面重新点「启动」；" & vbCrLf &
                    $"3. 如果仍然失败，重启电脑再试（WinDivert 驱动需要重启才会完全释放）。" & vbCrLf & vbCrLf &
                    $"错误详情：{LastError?.Message}")
                Return
            End If
            '重命名同样可能撞锁，一并重试
            For Attempt As Integer = 1 To 6
                Try
                    If FileUtils.Exists(TargetDir & "easytier-core.exe") Then
                        FileUtils.Move(TargetDir & "easytier-core.exe", TargetDir & "联机模块.exe")
                    End If
                    If FileUtils.Exists(TargetDir & "easytier-cli.exe") Then
                        FileUtils.Move(TargetDir & "easytier-cli.exe", TargetDir & "联机模块 CLI.exe")
                    End If
                    Exit For
                Catch ex As Exception
                    LastError = ex
                    Logger.Warn(ex, $"联机模块改名第 {Attempt}/6 次失败，稍后重试")
                    ProcessStop()
                    Thread.Sleep(400)
                    If Attempt = 6 Then
                        Panic("联机模块改名失败",
                            $"没法把 easytier-core.exe 改名为 {vbLQ}联机模块.exe{vbRQ}，文件可能被占用。{vbCrLf}{vbCrLf}" &
                            $"请结束任务管理器里 {vbLQ}联机模块{vbRQ} 开头的进程后重试，或重启电脑。{vbCrLf}{vbCrLf}" &
                            $"错误详情：{ex.Message}")
                        Return
                    End If
                End Try
            Next
            '清理
            FileUtils.Delete(PathEasyTier & "EasyTier.zip")
            Settings.Set("LinkEasyTierVersion", ServerVersion)
            '原地解压成功就把备用目录标记清掉，下次回到主目录
            If TargetDir = PathEasyTier Then EasyTierRunDirOverride = ""
        End If
        Task.Progress = 0.07
        '获取节点列表
        UpdateLoadingPage("正在获取节点列表……", "获取节点列表")
        '节点来源由「设置 → 联机 → 使用节点」决定：
        '  自建节点 -> 只用用户自己填的地址，服务端下发的节点不参与
        '  自动 / 指定某个服务端节点 -> 用服务端下发的节点（LinkNodePeers 已按选择过滤）
        Dim UseCustom As Boolean = LinkUseCustomNode()
        Dim CustomPeerList As List(Of String) = LinkCustomPeerList()
        Dim ServerPeers As List(Of String) = LinkNodePeers()
        Dim RawPeers As List(Of String)
        If UseCustom Then
            If CustomPeerList.Count = 0 Then
                Panic("自建节点还没填地址",
                    $"当前的「使用节点」是 {vbLQ}自建节点{vbRQ}，但没有填写任何中继地址。{vbCrLf}{vbCrLf}" &
                    $"请打开 {vbLQ}设置 → 联机 → 高级设置 → 自定义节点{vbRQ} 填写，{vbCrLf}" &
                    $"格式如 tcp://节点地址:11010。")
                Return
            End If
            If DiscoverNodeID <> -2 AndAlso Not IsServerSide Then
                Hint("你用的是自建节点，但邀请码显示房主没用。请确认双方设置一致。", HintType.Red)
            End If
            RawPeers = CustomPeerList
            Logger.Info($"使用自建节点：{String.Join(", ", RawPeers)}")
        Else
            If ServerPeers.Count = 0 Then
                Panic("没有可用的联机节点",
                    $"服务端没有下发任何可用节点，你也没有选择 {vbLQ}自建节点{vbRQ}。{vbCrLf}{vbCrLf}" &
                    $"要么在后台给这个启动器配置节点，要么把 {vbLQ}设置 → 联机 → 使用节点{vbRQ}{vbCrLf}" &
                    $"改成 {vbLQ}自建节点{vbRQ} 并自己填一个中继地址。")
                Return
            End If
            If DiscoverNodeID = -2 AndAlso Not IsServerSide Then
                Panic("房主使用了自建节点",
                    $"房主用的是自建中继，你也必须用同样的地址。{vbCrLf}" &
                    $"请把 {vbLQ}设置 → 联机 → 使用节点{vbRQ} 改成 {vbLQ}自建节点{vbRQ}，{vbCrLf}" &
                    $"并在高级设置里填写与房主相同的中继地址。")
                Return
            End If
            RawPeers = ServerPeers
            '服务端节点是直连，本来没有「发现节点」概念，DiscoverNodeID 会停在 -1。
            '但 GetInviteCode 会把 -1 编码成 0（末尾 000），加入方把 000 解释成
            '「房主用了自定义节点」而误拦。这里给房主一个非零哨兵，让末尾变成 001。
            If IsServerSide Then DiscoverNodeID = 1
            Logger.Info($"使用服务端节点：{String.Join(", ", RawPeers)}")
        End If
        Task.Progress = 0.13
        '获取空闲端口
        UpdateLoadingPage("正在启动联机模块……", "启动联机模块")
        'EasyTier 从 --listeners 的端口开始实际会占用 4 个连续端口
        '（实测 41231→tcp/udp、41232→wg/ws、41233→quic/wss、41234→faketcp），
        '所以这里预留 6 个：Client(0)、RPC(1)、Listeners(2..5)。
        Dim FreePorts = FindFreePorts(6, ServerPort)
        ClientPort = FreePorts(0)
        RPCPort = FreePorts(1)
        Dim ListenersPort As Integer = FreePorts(2) '它会占用【当前 .. 当前 +3】共四个端口
        '获取启动参数
        Dim Arguments As String = ServerConfig("Link")("Argument")
        Arguments += $" --network-name={NetworkName} --network-secret={NetworkSecret} --listeners {ListenersPort} --rpc-portal {RPCPort} --private-mode true"
        Dim HostName = If(IsServerSide, "Server-", "Client-") & Identify.GetStableHashCode().ConvertRadix(36).TrimStart("-"c).EnsureLength("0", 6)
        If IsServerSide Then
            Arguments += $" -i 10.114.114.114 --hostname={HostName} --tcp-whitelist={ServerPort} --udp-whitelist={ServerPort}"
        Else
            Arguments += $" -d --hostname={HostName} --tcp-whitelist=0 --udp-whitelist=0"
            '端口转发
            ClientAddress = $"localhost:{ClientPort}"
            Arguments += $" --port-forward tcp://[{IPAddress.IPv6Loopback}]:{ClientPort}/10.114.114.114:{ServerPort}"
            Arguments += $" --port-forward udp://[{IPAddress.IPv6Loopback}]:{ClientPort}/10.114.114.114:{ServerPort}"
            Arguments += $" --port-forward tcp://{IPAddress.Loopback}:{ClientPort}/10.114.114.114:{ServerPort}"
            Arguments += $" --port-forward udp://{IPAddress.Loopback}:{ClientPort}/10.114.114.114:{ServerPort}"
        End If
        For Each Peer As String In RawPeers
            Arguments += $" -p=""{Peer}"""
        Next
        If Settings.Get(Of Integer)("LinkLatencyMode") = 1 Then Arguments += " --latency-first"
        '启动进程
        ProcessStart(Arguments)
        Task.Progress = 0.15
        '等待连接
        Dim TimeoutCounter As Integer = 0
        Do
            Thread.Sleep(500) '为避免还没启动好先跑 CLI 了，先等半秒
            '刷新信息
            CheckCrash()
            RefreshPeerLoader.WaitForExit(IsForceRestart:=True)
            '查找目标节点
            Dim Ping = GetPeerPing()
            If Ping > 0 Then
                Logger.Info($"已与目标建立连接，当前 Ping 为 {Ping:0.0}ms")
                Telemetry("联机成功", "Server", IsServerSide, "NAT", NATType)
                Exit Do '退出循环
            End If
            '更新进度
            Dim LastProgress = Task.Progress
            Dim PeerCount As Integer = If(Peers Is Nothing, -1, Peers.Where(Function(p) p.Ping > 0).Count)
            Select Case PeerCount
                Case -1 'CLI 无返回
                    Task.Progress = (Task.Progress + 0.02).Clamp(0.15, 0.25)
                Case 0 'CLI 有返回，但未连接到任何节点
                    UpdateLoadingPage("正在连接到节点……", "连接节点")
                    Task.Progress = (Task.Progress + 0.02).Clamp(If(IsServerSide, 0.5, 0.3), If(IsServerSide, 0.95, 0.5))
                Case Else '已连接到节点，但未连接到房主
                    UpdateLoadingPage("正在连接到房主……", "连接房主")
                    Task.Progress = (Task.Progress + 0.02).Clamp(Math.Min(0.45 + PeerCount * 0.05, 0.65), 0.95)
            End Select
            '超时判定
            If LastProgress <> Task.Progress Then
                TimeoutCounter = 0
            ElseIf TimeoutCounter < 60 * 2 Then   '进度停滞 60 秒才判定失败（原为 30 秒）
                TimeoutCounter += 1
            Else '进度停滞超过 30s
                Select Case PeerCount
                    Case -1 'CLI 无返回
                        Panic("无法启动联机模块", "近期日志：" & vbCrLf & LogHistory.Join(vbCrLf))
                    Case 0 'CLI 有返回，但未连接到任何节点
                        Dim BriefLog As String = If(LogHistory.Count > 0,
    vbCrLf & vbCrLf & "近期模块日志：" & vbCrLf & LogHistory.Join(vbCrLf), "")
Panic("无法连接到节点", "请检查你的网络环境是否良好，稍后重试。" & BriefLog)
                    Case Else '已连接到节点，但未连接到房主
                        Panic("无法连接到房主", $"可能的原因：{vbCrLf}- 你或者房主的网络环境不佳{vbCrLf}- 房主已关闭房间{vbCrLf}- 邀请码输错了")
                End Select
            End If
        Loop Until Task.IsCanceled
        If Task.IsCanceled Then Throw New OperationCanceledException
        '等待连接稳定，最多 5s
        If IsServerSide Then
            '房主自检：确认本机这个端口真的有程序在监听（即 Minecraft 的「对局域网开放」）。
            '没有监听的话，加入方连过来必然失败，只会看到 Minecraft 的
            '「Connection reset」。提前提示比让对方自己猜要好。
            Dim Listening As Boolean = False
            Try
                Using Client As New TcpClient()
                    Dim ConnTask As Threading.Tasks.Task = Client.ConnectAsync("127.0.0.1", ServerPort)
                    Listening = ConnTask.Wait(800)
                End Using
            Catch
                Listening = False
            End Try
            If Listening Then
                Logger.Info($"房主自检通过：127.0.0.1:{ServerPort} 正在监听")
            Else
                Logger.Warn($"房主自检失败：127.0.0.1:{ServerPort} 没有程序在监听")
                RunInUiWait(Sub() Hint(
                    $"本机 {ServerPort} 端口没有程序在监听。" & vbCrLf &
                    $"请先在游戏里点「对局域网开放」，并确认端口与这里填写的一致，" & vbCrLf &
                    $"否则加入者会连接失败。", HintType.Red))
            End If
            Return
        End If
        UpdateLoadingPage("连接优化中……", "优化连接")
        Task.Progress = 0.999
        For i = 1 To 50
            Dim Server = GetTargetPeer()
            If Server IsNot Nothing AndAlso Not Server.Relay AndAlso Server.Ping < 100 Then Return '结束
            If Task.IsCanceled Then Throw New OperationCanceledException
            Thread.Sleep(100)
        Next
    End Sub
    ''' <summary>
    ''' 从在线配置和 API 获取需要连接的节点列表，并更新发现节点 ID。
    ''' 根据发现节点 ID，会有以下行为变化：
    '''   -1：作为房主，根据负载均衡选择一个发现节点。
    '''   0：作为加入者，但邀请码未提供发现节点 ID，使用回退发现节点。
    '''   >0：作为加入者，根据 ID 选择对应的发现节点；如果没有，使用回退发现节点。
    ''' </summary>
    Private Function GetTargetPeers() As List(Of String)
        Dim FinalPeers As New List(Of String), FinalDiscoverID As Integer = -1
        Dim FallbackDiscoverID = ServerConfig("Link")("DiscoverPeerId").ToObject(Of Integer)
        Dim FallbackDiscoverAddress As String = ServerConfig("Link")("DiscoverPeer").ToString()
        Try
            '从 API 获取节点列表
            Dim RawNodes As JObject
            Dim IsFallbackRawList As Boolean = False
            Try
                RawNodes = NetRequestByClient("https://uptime.easytier.cn/api/nodes?page=1&per_page=1000", RequireJson:=True).DeserializeJson()
            Catch exx As Exception
                Logger.Warn(exx, "从源站获取节点列表失败，将使用 CDN 缓存")
                RawNodes = NetRequestByClientRetry("https://easytier.meloong.com/?page=1&per_page=1000", RequireJson:=True).DeserializeJson()
                IsFallbackRawList = True
            End Try
            '分析节点列表
            Dim Nodes As New List(Of JObject) '负载会添加在 load 字段上
            Dim BlackList As List(Of String) = ServerConfig("Link")("PeersBlackList").Select(Function(p) p.ToString).ToList()
            For Each Node As JObject In RawNodes("data")("items")
                '状态检查
                Dim ID = Node("id").ToObject(Of Integer)
                If Not Node("is_active").ToObject(Of Boolean) OrElse Not Node("is_approved").ToObject(Of Boolean) Then Continue For
                If ID = DiscoverNodeID Then GoTo ForcedPass '若为指定的发现节点，忽略后续检查
                If ID = FallbackDiscoverID Then Continue For '不主动选取回退发现节点
                If BlackList.Contains(Node("address").ToString) Then Continue For 'ServerConfig 黑名单
                If Node("usage_percentage").ToObject(Of Double) = 0 AndAlso RandomInteger(1, 100) <> 1 Then Continue For '或许节点有问题才导致是 0 负载，让它只有 1% 概率被选中
                '标签检查
                Dim Tags = Node("tags").Select(Function(t) t.ToString).ToList
                If Not Tags.Contains("国内") OrElse Not Tags.Contains("MC中继") Then Continue For
                '计算负载并加入列表
ForcedPass:
                Dim Load As Double = Node("usage_percentage").ToObject(Of Double) '负载
                Load *= 110 - Node("health_percentage_24h").ToObject(Of Double) '可用率
                Node("load") = Load
                Nodes.Add(Node)
            Next
            '排序
            If Not IsFallbackRawList Then
                Nodes = Nodes.OrderBy(Function(n) n("load").ToObject(Of Double)).ToList() '按负载从低到高排序
            Else
                Nodes = Nodes.Shuffle().ToList() '回退到 CDN 缓存时，由于负载数据可能过期，直接随机选择
            End If
            '选取发现节点
            Dim SelectedDiscoverNode As JObject = Nothing
            If DiscoverNodeID = -1 Then '-1：作为房主，根据负载均衡选择一个发现节点
                SelectedDiscoverNode = Nodes.FirstOrDefault(Function(n) Not n("allow_relay").ToObject(Of Boolean))
            ElseIf DiscoverNodeID > 0 Then '>0：作为加入者，根据 ID 选择对应的发现节点；如果没有，使用回退发现节点
                SelectedDiscoverNode = Nodes.FirstOrDefault(Function(n) n("id").ToObject(Of Integer) = DiscoverNodeID)
                If SelectedDiscoverNode Is Nothing Then
                    Logger.Warn($"未找到 ID {DiscoverNodeID} 的发现节点")
                    Panic("房间已过期", "请让房主重新创建房间！")
                    Throw New OperationCanceledException
                End If
            End If
            If SelectedDiscoverNode Is Nothing Then '使用回退发现节点
                SelectedDiscoverNode = New JObject From {{"address", FallbackDiscoverAddress}, {"id", FallbackDiscoverID}}
                Logger.Warn("将使用回退发现节点")
            End If
            FinalPeers.Add(SelectedDiscoverNode("address").ToString())
            FinalDiscoverID = SelectedDiscoverNode("id").ToObject(Of Integer)
            Logger.Info($"发现节点：{SelectedDiscoverNode("address")} (ID: {FinalDiscoverID})")
            '选取中继节点
            If ModeDebug OrElse Not IsServerSide Then '房主只连接发现节点，不连接中继节点
                Dim RelayCount As Integer = ServerConfig("Link")("RandomPeer").ToObject(Of Integer)
                Dim RelayNodes = Nodes.Where(Function(n) n("allow_relay").ToObject(Of Boolean)).ToList()
                If RelayNodes.Count < RelayCount Then Throw New Exception($"可用的中继节点数量不足，需要 {RelayCount} 个，实际 {RelayNodes.Count} 个")
                FinalPeers.AddRange(RelayNodes.Take(RelayCount).Select(Function(n) n("address").ToString()))
            End If
        Catch ex As Exception
            ex.ThrowIfCanceled()
            Logger.Error(ex, "获取节点列表失败，联机质量可能受到影响", LogBehavior.Toast)
            FinalPeers.AddRange(ServerConfig("Link")("Peers").Select(Function(p) p.ToString))
            If FinalDiscoverID <= 0 Then
                FinalPeers.Add(FallbackDiscoverAddress)
                FinalDiscoverID = FallbackDiscoverID
            End If
        End Try
        '版本 1 兼容
        If CType(ServerConfig("Link"), JObject).ContainsKey("EnableForcedPeer") Then
            FinalPeers.Add("tcp://mc1.easytier.cn:55558")
            If FinalDiscoverID <= 0 Then FinalDiscoverID = 94
        End If
        '强制添加的节点
        FinalPeers.AddRange(ServerConfig("Link")("MandatoryPeers").Select(Function(p) p.ToString))
        '结束
        DiscoverNodeID = FinalDiscoverID
        Return FinalPeers.Where(Function(p) Not String.IsNullOrWhiteSpace(p)).Distinct.ToList
    End Function

#End Region

#Region "进程与日志"

    ''' <summary>
    ''' 用提权的 taskkill 结束进程（会弹出一次 UAC）。用于结束以管理员身份运行的联机模块。
    ''' </summary>
    Private Shared Function KillElevated(Pid As Integer) As Boolean
        Try
            '提权启动时外层是 cmd.exe，模块是它的子进程，必须 /T 一起结束
            Dim Info As New ProcessStartInfo("cmd.exe", $"/c taskkill /F /T /PID {Pid}") With {
                .UseShellExecute = True,
                .Verb = "runas",
                .WindowStyle = ProcessWindowStyle.Hidden,
                .CreateNoWindow = True
            }
            Using P = Process.Start(Info)
                P?.WaitForExit(10000)
            End Using
            Logger.Info($"已通过提权 taskkill 结束联机模块，PID：{Pid}")
            Return True
        Catch ex As Exception
            Logger.Warn(ex, "提权结束联机模块失败（用户可能取消了 UAC）")
            Return False
        End Try
    End Function

    ''' <summary>
    ''' EasyTier Core 进程。
    ''' </summary>
    ''' <summary>
    ''' 联机模块的备用运行目录（原目录被占用时切到 alt-&lt;版本&gt;）。
    ''' 只在本次运行内有效，不写入配置，避免残留一个已失效的路径。
    ''' </summary>
    Private Shared EasyTierRunDirOverride As String = ""

    ''' <summary>提权启动时模块输出被重定向到的文件。</summary>
    Private Shared ModuleLogPath As String = ""

    ''' <summary>日志回读线程是否继续。</summary>
    Private Shared ModuleLogTailing As Boolean = False

    ''' <summary>
    ''' 回读提权启动时写进文件的模块输出，喂给 ProcessLogLine。
    ''' 这样「无法连接」面板里能直接看到 EasyTier 的真实报错。
    ''' </summary>
    Private Sub StartModuleLogTail()
        If String.IsNullOrEmpty(ModuleLogPath) Then Return
        ModuleLogTailing = True
        Dim T As New Threading.Thread(
            Sub()
                Dim Position As Long = 0
                Dim Carry As String = ""
                Do While ModuleLogTailing
                    Threading.Thread.Sleep(250)
                    Try
                        If Not FileUtils.Exists(ModuleLogPath) Then Continue Do
                        Using Fs As New IO.FileStream(ModuleLogPath, IO.FileMode.Open, IO.FileAccess.Read,
                                                      IO.FileShare.ReadWrite)
                            If Fs.Length <= Position Then Continue Do
                            Fs.Seek(Position, IO.SeekOrigin.Begin)
                            Using Reader As New IO.StreamReader(Fs, Encoding.UTF8)
                                Dim Chunk As String = Reader.ReadToEnd()
                                Position = Fs.Position
                                Dim Text As String = Carry & Chunk
                                Dim Lines = Text.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
                                '最后一段可能还没写完，留到下一轮
                                Carry = Lines(Lines.Length - 1)
                                For i = 0 To Lines.Length - 2
                                    If Lines(i).Trim() <> "" Then ProcessLogLine(Lines(i))
                                Next
                            End Using
                        End Using
                    Catch ex As Exception
                        Logger.Warn(ex, "回读联机模块日志失败")
                    End Try
                Loop
            End Sub)
        T.IsBackground = True
        T.Name = "LinkModuleLogTail"
        T.Start()
    End Sub

    Private Shared Sub StopModuleLogTail()
        ModuleLogTailing = False
    End Sub
    Private Shared ProcessCore As Process
    Private Shared ProcessOutputHandle As AutoResetEvent, ProcessErrorHandle As AutoResetEvent

    ''' <summary>
    ''' 若程序正在运行，则结束程序进程。
    ''' 即使失败也不会抛出异常。
    ''' </summary>
    Public Shared Sub ProcessStop()
        Try
            StopModuleLogTail()
            '关闭未捕获的进程
            For Each ProcessObject In Process.GetProcesses
                If ProcessObject.Id = ProcessCore?.Id Then Continue For
                If ProcessObject.ProcessName <> "联机模块" AndAlso ProcessObject.ProcessName <> "联机模块 CLI" Then Continue For
                Try
                    Logger.Info($"停止残留的联机模块，PID：{ProcessObject.Id}，进程名：{ProcessObject.ProcessName}")
                    ProcessObject.Kill()
                    '必须等它真的退出：WinDivert64.sys 在进程消失后还要一小会儿才释放
                    ProcessObject.WaitForExit(3000)
                    ProcessObject.Close()
                Catch exx As Exception
                    Logger.Warn(exx, $"结束进程失败（{ProcessObject.ProcessName}，PID {ProcessObject.Id}）")
                End Try
            Next
            '关闭由自身启动的进程
            If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then
                Dim StoppedPid As Integer = -1
                Try : StoppedPid = ProcessCore.Id : Catch : End Try
                Logger.Info($"停止所启动的联机模块，PID：{StoppedPid}")
                Try
                    ProcessCore.Kill()
                    ProcessCore.WaitForExit(3000)
                Catch exx As Exception
                    '模块以管理员身份运行时，本程序无权结束它，退化为提权 taskkill（会弹一次 UAC）
                    Logger.Warn(exx, "直接结束联机模块失败，尝试提权结束")
                    If StoppedPid > 0 Then KillElevated(StoppedPid)
                End Try
                Try : ProcessCore.Close() : Catch : End Try
                ProcessCore = Nothing
            End If
            '释放资源
            ProcessOutputHandle?.Dispose()
            ProcessErrorHandle?.Dispose()
        Catch ex As Exception
            Logger.Warn(ex, "停止联机模块失败")
        End Try
    End Sub
    ''' <summary>
    ''' 出现意外错误，给出错误信息并结束联机。
    ''' </summary>
    Private Sub Panic(Brief As String, Detail As String)
        '常见原因分析
        If Detail.Contains("failed to listen on ") Then
            Detail = $"监听端口失败。{vbCrLf}请点击重试，如果还是出现此错误，可以重启电脑解决。{vbCrLf}{vbCrLf}{Detail}"
        End If
        '显示信息
        If LinkState = LinkStates.Loading Then
            FailReason = Brief
            LinkLoader.Failed(New Exception(Detail))
        Else
            MyMsgBox(Detail, "联机出错：" & Brief, IsWarn:=True)
            ChangeState(LinkStates.Waiting)
            Telemetry("联机失败", "Exception", FilterUserName(Brief & "：" & Detail, "*"))
        End If
    End Sub

    ''' <summary>
    ''' 启动进程。
    ''' </summary>
    Private Sub ProcessStart(Arguments As String)
        Dim ModulePath As String = ModuleDir() & "联机模块.exe"
        Logger.Info($"正在启动 EasyTier：{Arguments}")
        If WindowsUtils.HasAdminRole() Then
            '已是管理员：直接启动并接管输出（原逻辑不变）
            Dim Info = New ProcessStartInfo With {
                .FileName = ModulePath,
                .Arguments = Arguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardError = True,
                .RedirectStandardOutput = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8,
                .WorkingDirectory = ModuleDir()
            }
            ProcessCore = New Process With {.StartInfo = Info}
            Dim LogLineHandler =
            Sub(sender As Object, e As DataReceivedEventArgs, Handle As AutoResetEvent)
                Try
                    If e.Data Is Nothing Then
                        Handle.[Set]()
                        Return
                    End If
                    ProcessLogLine(e.Data)
                Catch unused As ObjectDisposedException
                Catch ex As Exception
                    Logger.Warn(ex, "读取 EasyTier 信息失败")
                End Try
            End Sub
            ProcessOutputHandle = New AutoResetEvent(False)
            AddHandler ProcessCore.OutputDataReceived, Sub(sender, e) LogLineHandler(sender, e, ProcessOutputHandle)
            ProcessErrorHandle = New AutoResetEvent(False)
            AddHandler ProcessCore.ErrorDataReceived, Sub(sender, e) LogLineHandler(sender, e, ProcessErrorHandle)
            ProcessCore.Start()
            ProcessCore.BeginOutputReadLine()
            ProcessCore.BeginErrorReadLine()
        Else
            '不是管理员：只对联机模块本身提权，主程序保持非管理员，不再重启自己。
            '提权启动没法直接重定向 stdout，所以用 cmd 包一层把输出写到文件，再回读。
            Logger.Info("当前非管理员，使用 runas 启动联机模块（会弹出一次 UAC）")
            ModuleLogPath = Paths.BaseThenName & "LinkModule.log"
            Try
                FileUtils.Delete(ModuleLogPath)
            Catch
            End Try
            Dim QuoteMark As String = Chr(34)
            Dim EscapedExe As String = QuoteMark & ModulePath & QuoteMark
            Dim EscapedLog As String = QuoteMark & ModuleLogPath & QuoteMark
            'cmd /c 的字符串以引号开头时，它的解析规则会吃掉首尾引号，导致整条命令失效
            '（实测：/c "exe" args > "log" 退出码 1、日志不生成）。
            '必须用 /s /c 并把整条命令再包一层引号：/s /c ""exe" args > "log" 2>&1"
            Dim CmdArgs As String = $"/s /c {QuoteMark}{EscapedExe} {Arguments} > {EscapedLog} 2>&1{QuoteMark}"
            Dim Info As New ProcessStartInfo("cmd.exe", CmdArgs) With {
                .UseShellExecute = True,
                .Verb = "runas",
                .WindowStyle = ProcessWindowStyle.Hidden,
                .CreateNoWindow = True,
                .WorkingDirectory = ModuleDir()
            }
            ProcessCore = New Process With {.StartInfo = Info}
            Try
                ProcessCore.Start()
                Dim StartedPid As Integer = -1
                Try : StartedPid = ProcessCore.Id : Catch : End Try
                Logger.Info($"联机模块已请求提权启动，PID：{StartedPid}，模块日志：{ModuleLogPath}")
                StartModuleLogTail()
            Catch ex As Exception
                ProcessCore = Nothing
                Throw New Exception("未能获得管理员权限，联机模块无法启动。" & vbCrLf &
                    "请在弹出的 UAC 窗口中点「是」；如果压根没看到弹窗，可以右键 MITC.exe 选择「以管理员身份运行」后再试。" & vbCrLf & vbCrLf & ex.Message, ex)
            End Try
        End If
    End Sub
    Private Sub ProcessLogLine(Line As String)
        '记录日志
        Logger.Info(Line)
        LogHistory.Enqueue(Line)
        If LogHistory.Count >= 10 Then LogHistory.Dequeue()
        '检查日志内容
        If Line.ContainsIgnoreCase("new peer connection added") Then
            Logger.Warn($"已建立连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        ElseIf Line.ContainsIgnoreCase("peer connection removed") Then
            Logger.Warn($"已断开连接：{If(Line.RegexSeek("(?<=remote_addr.+?"")[^""}]{3,}"), Line)}")
            Update()
        End If
    End Sub
    Private LogHistory As New Queue(Of String)(10)

#End Region

#Region "CLI 节点信息获取"

    Private Class Peer
        ''' <summary>
        ''' 该节点的类型。
        ''' </summary>
        Public ReadOnly Type As Types
        Public Enum Types
            ''' <summary>
            ''' 客户端。
            ''' </summary>
            Client
            ''' <summary>
            ''' 服务器。
            ''' </summary>
            Server
            ''' <summary>
            ''' 公共节点。
            ''' </summary>
            Misc
            ''' <summary>
            ''' 自己，或者自己的重复（它可能返回多个自己）。
            ''' </summary>
            Self
        End Enum
        ''' <summary>
        ''' 与该节点的延迟 (ms)。
        ''' 若是自己则为 0。
        ''' </summary>
        Public ReadOnly Ping As Double
        ''' <summary>
        ''' 名称。
        ''' </summary>
        Public ReadOnly Name As String
        ''' <summary>
        ''' 是否通过中继连接。
        ''' </summary>
        Public ReadOnly Relay As Boolean
        ''' <summary>
        ''' NAT 类型。
        ''' </summary>
        Public ReadOnly NATType As NATTypes

        ''' <summary>
        ''' 从 CLI 给出的信息分析对应的数据。
        ''' </summary>
        Public Sub New(Info As JObject)
            '类别
            Dim PeerName = Info("hostname").ToString
            If Info("cost").ToString = "Local" Then
                Type = Types.Self
            ElseIf PeerName.StartsWithF("Client") Then
                Type = Types.Client
            ElseIf Info("ipv4") = "10.114.114.114" Then '只允许这个 IP 作为房主，避免网络被多个房主搞炸的情况
                Type = Types.Server
            Else
                Type = Types.Misc
            End If
            '基础信息
            Double.TryParse(Info("lat_ms"), NumberStyles.Any, CultureInfo.InvariantCulture, Ping)
            Name = PeerName
            Relay = Info("cost").ToString.ContainsIgnoreCase("relay")
            NATType = EnumUtils.FromString(Of NATTypes)(Info("nat_type"))
        End Sub
        Public Overrides Function ToString() As String
            Return $"{Type} - {Name} - Ping {Ping:0.0}ms [中继? {Relay}] - NAT {NATType}"
        End Function
    End Class
    Private Enum NATTypes
        'https://github.com/EasyTier/EasyTier/blob/6bb2fd9a15ab2499bdeabdcc3a925e9bd9aebf50/easytier/src/proto/common.proto#L129
        Unknown = 0
        OpenInternet = 1
        NoPAT = 2
        FullCone = 3
        Restricted = 4
        PortRestricted = 5
        Symmetric = 6
        SymUdpFirewall = 7
        SymmetricEasyInc = 8
        SymmetricEasyDec = 9
        ''' <summary>
        ''' 尚未获取。
        ''' </summary>
        Pending = 10
    End Enum

    ''' <summary>
    ''' 当前的节点列表，使用 RefreshPeerLoader 来刷新。
    ''' 若尚未成功获取过则为 Nothing，但保证在加载完成后至少是一个列表。
    ''' </summary>
    Private Peers As List(Of Peer) = Nothing
    ''' <summary>
    ''' 自己的 NAT 类型。
    ''' </summary>
    Private NATType As NATTypes = NATTypes.Pending
    ''' <summary>
    ''' 断线容忍：跨省/跨境链路抖动时，EasyTier 的节点列表可能短暂为空，
    ''' 几秒内会自行恢复。原来只要有一秒为空就判定断开，导致「一波动就退出」。
    ''' 这里改为连续这么多秒都为空才真正断开。
    ''' </summary>
    Private Const DisconnectGraceSeconds As Integer = 10
    ''' <summary>节点列表连续为空的秒数。</summary>
    Private PeerEmptySeconds As Integer = 0
    ''' <summary>房主节点连续缺失的秒数。</summary>
    Private HostMissingSeconds As Integer = 0

    ''' <summary>
    ''' 调用 EasyTier CLI 获取已连接节点信息。
    ''' 这会过滤掉尚未完成连接的其他节点以及自己。
    ''' 若在加载完成后连续获取失败 3 次，则会强制断开连接。
    ''' </summary>
    Private RefreshPeerLoader As New LoaderTask(Of Integer, List(Of Peer))("EasyTier CLI", AddressOf RefreshPeer)
    ''' <summary>
    ''' 运行命令行程序并读取标准输出。超时则杀掉进程并返回空字符串。
    ''' 开源版把上游的 StartProcessAndGetOutput 一并删掉了，这里补一个最小实现。
    ''' </summary>
    Private Shared Function RunCliAndGetOutput(FileName As String, Arguments As String, TimeoutMs As Integer) As String
        Try
            If Not IO.File.Exists(FileName) Then
                Logger.Warn($"CLI 不存在：{FileName}")
                Return ""
            End If
            Dim Psi As New Diagnostics.ProcessStartInfo(FileName, Arguments) With {
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .StandardOutputEncoding = System.Text.Encoding.UTF8,
                .StandardErrorEncoding = System.Text.Encoding.UTF8}
            Using P As Diagnostics.Process = Diagnostics.Process.Start(Psi)
                Dim Reader As Threading.Tasks.Task(Of String) = P.StandardOutput.ReadToEndAsync()
                If P.WaitForExit(TimeoutMs) Then Return Reader.GetAwaiter().GetResult()
                Logger.Warn($"CLI 调用超时（{TimeoutMs}ms）：{Arguments}")
                Try
                    P.Kill()
                Catch
                End Try
            End Using
            Return ""
        Catch ex As Exception
            Logger.Warn(ex, "调用 EasyTier CLI 失败")
            Return ""
        End Try
    End Function

    Private Sub RefreshPeer()
        Try
            '开源版把这一行注释掉了，CliResult 恒为空，导致 Peers 永远是 Nothing，
            '联机必然在 30 秒后报「无法启动联机模块」。这里恢复真实调用。
            Dim CliResult As String = RunCliAndGetOutput(
                ModuleDir() & "联机模块 CLI.exe", $"-o json -p 127.0.0.1:{RPCPort} peer", 4000)
            '解析
            If Not CliResult.Contains("lat_ms") Then Throw New Exception("CLI 调用失败：" & vbCrLf & CliResult)
            If GetUuid() Mod If(ModeDebug, 23, 103) = 0 Then Logger.Info($"CLI 输出抽样：{vbCrLf}{CliResult}")
            Dim NewPeers As New List(Of Peer)
            For Each Line As JObject In CType(CliResult.DeserializeJson(), JArray)
                Try
                    Dim Peer = New Peer(Line)
                    If Peer.Type = Peer.Types.Self Then
                        NATType = Peer.NATType '记录自己的 NAT
                    Else
                        NewPeers.Add(Peer)
                    End If
                Catch exx As Exception
                    Logger.Warn(exx, $"错误的信息（{Line}）")
                End Try
            Next
            '完成
            Peers = NewPeers
            FailCount = 0
        Catch ex As Exception
            Logger.Warn(ex, "获取节点信息失败")
            If LinkState = LinkStates.Finished Then
                FailCount += 1
                '原来连续 4 次（约 4 秒）就断开。链路抖动时 CLI 偶发超时很常见，
                '放宽到连续 15 次再判定，避免一次网络波动就把人踢出房间。
                If FailCount >= 15 Then
                    Logger.Warn($"获取节点信息连续失败 {FailCount} 次，判定联机模块异常")
                    Panic("获取节点信息失败", ex.Message)
                End If
            End If
        End Try
    End Sub
    ''' <summary>
    ''' CLI 调用连续失败的次数。
    ''' </summary>
    Private FailCount As Integer = 0

    ''' <summary>
    ''' 根据当前的节点列表获取 Ping 值。
    ''' 服务端会返回所有节点中最低的 Ping，客户端会返回服务端的 Ping。
    ''' 0 代表尚不可用。
    ''' </summary>
    Private Function GetPeerPing() As Double
        Dim Peer = GetTargetPeer()
        If Peer Is Nothing Then Return 0
        Return Peer.Ping
    End Function
    ''' <summary>
    ''' 服务端会返回所有节点中 Ping 大于 0 最低的那一个，客户端会返回服务端。
    ''' 若没有则为 Nothing。
    ''' </summary>
    Private Function GetTargetPeer() As Peer
        If Peers Is Nothing Then Return Nothing
        Dim Targets As IEnumerable(Of Peer)
        If IsServerSide Then
            Targets = Peers.Where(Function(p) p.Ping > 0)
        Else
            Targets = Peers.Where(Function(p) p.Type = Peer.Types.Server AndAlso p.Ping > 0)
        End If
        If Not Targets.Any Then Return Nothing
        '返回 Ping 且最低的那个
        Dim MinPing = Targets.Min(Function(p) p.Ping)
        Return Targets.First(Function(p) p.Ping = MinPing)
    End Function

    ''' <summary>
    ''' 获取当前房间中的人数，包括自己。
    ''' 0 代表不可用。
    ''' </summary>
    Private ReadOnly Property PeopleCount As Integer
        Get
            If Peers Is Nothing Then Return 0
            '只数房间成员（房主 + 加入者），并按主机名去重：
            'EasyTier 会把同一个节点按不同路径（直连、走中继）各报一条，
            '直接 Count() 会把同一个人算成两个，出现「两个人显示三个人」。
            '中继节点本身是 Misc 类型，不计入。
            Dim Members = Peers.
                Where(Function(p) p.Type <> Peer.Types.Misc).
                Select(Function(p) p.Name).
                Where(Function(n) Not String.IsNullOrWhiteSpace(n)).
                Distinct().
                Count()
            Return Members + 1 '加上自己
        End Get
    End Property

#End Region

#Region "定时任务"

    '启动
    Private Sub StartTimerThread() Handles Me.Loaded
        Static IsTimerStarted As Boolean = False
        If IsTimerStarted Then Return
        RunInNewThread(
        Sub()
            Dim Counter As Integer = 0
            Do While True
                Try
                    Thread.Sleep(200)
                    Counter += 200
                    '每秒执行一次
                    If Counter >= 1000 Then
                        Counter = 0
                        Update()
                    End If
                    '每 200ms 更新进度条
                    If LinkState = LinkStates.Loading Then RunInUi(AddressOf UpdateProgressBar)
                Catch ex As Exception
                    Logger.Error(ex, "联机模块主时钟出错")
                    Thread.Sleep(10000)
                End Try
            Loop
        End Sub, "Link Timer")
    End Sub

    '每秒或进入页面时触发
    Private Sub Update() Handles Me.Loaded
        If LinkState <> LinkStates.Finished Then Return
        '重新获取信息
        SyncLock RefreshPeerLoader.LockState
            If RefreshPeerLoader.State <> LoadState.Loading Then RefreshPeerLoader.Start(IsForceRestart:=True)
        End SyncLock
        '更新 UI
        If FrmMain.PageCurrent = FormMain.PageType.Link Then
            RunInUi(
            Sub()
                'Logo 旋转动画
                AniStart(AaRotateTransform(ImgFinishLogo, 500, 5000), "Link Logo Rotation")
                'Ping
                Dim Ping As Double = GetPeerPing()
                Dim Connecting As Boolean = Ping Mod 500 = 0 OrElse $"{Ping:0.0}" = "1.0" OrElse FailCount > 0
                If Connecting Then
                    LabFinishPing.Text = "连接优化中"
                Else
                    LabFinishPing.Text = If(Ping >= 10, $"{Ping:0} ms", $"{Ping:0.0} ms")
                End If
                '人数显示
                LabFinishPlayer.Text = PeopleCount & "  人"
                '------------------------
                '  提示条
                '------------------------
                HintFinish.Visibility = Visibility.Collapsed
                If IsServerSide Then Return '服务端没有需要显示的提示
                If Connecting Then Return '连接稳定过程中不显示提示
                '中继提示
                Dim Server As Peer = GetTargetPeer()
                If Server Is Nothing Then Return
                If Server.Relay Then
                    If NATType >= NATTypes.Symmetric AndAlso Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你和房主的网络环境都不太好，"
                    ElseIf NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "你的网络环境不太好，"
                    ElseIf Server.NATType >= NATTypes.Symmetric Then
                        HintFinish.Text = "房主的网络环境不太好，"
                    Else
                        HintFinish.Text = "你或者房主的网络环境不太好，"
                    End If
                    If String.IsNullOrWhiteSpace(Settings.Get(Of String)("LinkCustomPeer")) Then
                        HintFinish.Text &= "正使用社区节点进行中继。"
                    Else
                        HintFinish.Text &= "正通过自定义节点进行中继。"
                    End If
                    HintFinish.Visibility = Visibility.Visible
                    HintFinish.Theme = MyHint.Themes.Yellow
                    Return
                End If
                '环境比房主更好的提示
                If NATType >= NATTypes.PortRestricted Then Return '自身的 NAT 为 2 或更好
                If Server.NATType < NATTypes.PortRestricted Then Return '房主的 NAT 为 3 或更差
                Dim OtherPlayers = Peers.Where(Function(p) p.Type = Peer.Types.Client).ToList()
                If Not OtherPlayers.Any() Then Return '房间里还有其他玩家
                If OtherPlayers.Any(Function(p) p.Relay) Then Return '自己可以与所有玩家打洞
                If OtherPlayers.All(Function(p) p.NATType < NATTypes.PortRestricted) Then Return '任意其他玩家的 NAT 为 3 或更差
                HintFinish.Visibility = Visibility.Visible
                HintFinish.Theme = MyHint.Themes.Blue
                HintFinish.Text = "你的网络环境比房主更好！如果你来当房主，其他玩家或许能更加流畅！"
            End Sub)
        End If
        '检查核心状态
        CheckCrash()
        '检查节点状态。这里带容忍：网络波动会让节点列表短暂为空，
        '必须连续多秒为空才判定真的断开，否则任何抖动都会把用户踢出房间。
        If Peers Is Nothing OrElse Not Peers.Any Then
            PeerEmptySeconds += 1
            If PeerEmptySeconds < DisconnectGraceSeconds Then
                Logger.Warn($"节点列表为空，等待恢复（{PeerEmptySeconds}/{DisconnectGraceSeconds} 秒）")
                Return '本轮跳过后续广播等逻辑
            End If
            Logger.Warn($"节点列表连续 {PeerEmptySeconds} 秒为空，判定连接已断开")
            Panic("网络连接已断开", "请检查你的网络环境是否正常。")
            Return
        ElseIf PeerEmptySeconds > 0 Then
            Logger.Info($"节点列表已恢复（此前为空 {PeerEmptySeconds} 秒）")
            PeerEmptySeconds = 0
        End If
        If Not IsServerSide AndAlso Not Peers.Any(Function(p) p.Type = Peer.Types.Server) Then
            HostMissingSeconds += 1
            If HostMissingSeconds < DisconnectGraceSeconds Then
                Logger.Warn($"房主节点暂时不可见，等待恢复（{HostMissingSeconds}/{DisconnectGraceSeconds} 秒）")
                Return
            End If
            Logger.Warn($"房主节点连续 {HostMissingSeconds} 秒不可见，判定房主已离开")
            MyMsgBox("房主已离开房间！", "联机结束")
            ChangeState(LinkStates.Waiting)
            Return
        ElseIf HostMissingSeconds > 0 Then
            Logger.Info($"房主节点已恢复（此前缺失 {HostMissingSeconds} 秒）")
            HostMissingSeconds = 0
        End If
        '广播联机房间端口
        If Not IsServerSide Then
            Try
                Static BroadcastSocket As New Socket(SocketType.Dgram, ProtocolType.Udp)
                BroadcastSocket.SendTo(
                    Encoding.UTF8.GetBytes($"[MOTD]PCL MITC 联机房间[/MOTD][AD]{ClientPort}[/AD]"),
                    SocketFlags.None,
                    New IPEndPoint(IPAddress.Loopback, 4445))
            Catch ex As Exception
                Logger.Warn(ex, "广播联机房间端口失败")
            End Try
        End If
    End Sub
    ''' <summary>
    ''' 检查联机模块是否崩溃。
    ''' </summary>
    Private Sub CheckCrash()
        If ProcessCore IsNot Nothing AndAlso Not ProcessCore.HasExited Then Return
        Panic("联机模块已崩溃", "近期日志：" & vbCrLf & LogHistory.Join(vbCrLf))
    End Sub

#End Region

End Class
