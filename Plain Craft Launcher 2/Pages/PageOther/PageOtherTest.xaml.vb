Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text

''' <summary>
''' 百宝箱页面。
'''
''' 注意：上游开源版把这整页挖空了（XAML 只剩一行占位文字，本类 9 个方法全部替换成
''' “为便于维护，开源内容中不包含百宝箱功能……”），但调用点仍然存在：
'''   * MemoryOptimize      ← ModLaunch 启动前内存优化、ModEvent 事件
'''   * MemoryOptimizeInternal ← Application 的 --memory 子进程模式
'''   * Jrrp / RubbishClear / StartCustomDownload ← ModEvent 的自定义按钮事件
'''   * GetRandomHint / GetRandomCave ← 主页与启动界面的随机文案
''' 所以这些方法被掏空时，表现为「设置里勾了内存优化却什么都不发生」、
''' 「启动提示显示占位文字」。这里把它们补回可用实现。
''' </summary>
Public Class PageOtherTest

#Region "页面"

    Private Sub PageOtherTest_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        AddHandler BtnJrrp.Click, AddressOf BtnJrrp_Click
        AddHandler BtnMemory.Click, AddressOf BtnMemory_Click
        AddHandler BtnRubbish.Click, AddressOf BtnRubbish_Click
        AddHandler BtnDontClick.Click, AddressOf BtnDontClick_Click
        AddHandler BtnCave.Click, AddressOf BtnCave_Click
        AddHandler BtnPickFolder.Click, AddressOf BtnPickFolder_Click
        AddHandler BtnDownload.Click, AddressOf BtnDownload_Click
        AddHandler BtnOpenFolder.Click, AddressOf BtnOpenFolder_Click

        If String.IsNullOrWhiteSpace(TextFolder.Text) Then TextFolder.Text = DefaultDownloadFolder()
        LabCave.Text = GetRandomCave()
    End Sub

    Private Sub BtnJrrp_Click(sender As Object, e As RoutedEventArgs) Handles BtnJrrp.Click
        Jrrp()
    End Sub

    Private Sub BtnMemory_Click(sender As Object, e As RoutedEventArgs)
        '清空工作集要动其他进程，没有管理员权限会大量失败，先提醒
        If Not WindowsUtils.HasAdminRole() Then
            If MyMsgBox("内存优化需要管理员权限，否则大部分进程会被跳过。" & vbCrLf & vbCrLf &
                        "是否仍要继续？", "权限提示", "继续", "取消") <> 1 Then Return
        End If
        MemoryOptimize(True)
    End Sub

    Private Sub BtnRubbish_Click(sender As Object, e As RoutedEventArgs)
        RubbishClear()
    End Sub

    Private Sub BtnDontClick_Click(sender As Object, e As RoutedEventArgs)
        '纯玩笑，不进行任何破坏性操作
        If MyMsgBox("你点了。" & vbCrLf & vbCrLf & "这个按钮会……清理所有存档？删掉 Java？把内存条拔出来？" &
                    vbCrLf & vbCrLf & "……其实什么都不会发生。", "千万别点", "我不信", "谢天谢地") = 1 Then
            Hint("真的什么都没发生。不过你的好奇心暴露了。", HintType.Blue)
        Else
            Hint("明智的选择。", HintType.Green)
        End If
    End Sub

    Private Sub BtnCave_Click(sender As Object, e As RoutedEventArgs)
        LabCave.Text = GetRandomCave()
    End Sub

    Private Sub BtnPickFolder_Click(sender As Object, e As RoutedEventArgs)
        Using Dlg As New System.Windows.Forms.FolderBrowserDialog()
            Dlg.Description = "选择下载文件的保存位置"
            Dlg.ShowNewFolderButton = True
            If Directory.Exists(TextFolder.Text.Trim) Then Dlg.SelectedPath = TextFolder.Text.Trim
            If Dlg.ShowDialog() = System.Windows.Forms.DialogResult.OK Then TextFolder.Text = Dlg.SelectedPath
        End Using
    End Sub

    Private Sub BtnDownload_Click(sender As Object, e As RoutedEventArgs)
        StartCustomDownload(TextUrl.Text, TextFileName.Text, TextFolder.Text)
    End Sub

    Private Sub BtnOpenFolder_Click(sender As Object, e As RoutedEventArgs)
        Dim Folder As String = TextFolder.Text.Trim
        If Folder.Length = 0 Then Folder = DefaultDownloadFolder()
        Try
            Directory.CreateDirectory(Folder)
            Process.Start("explorer.exe", Folder)
        Catch ex As Exception
            Hint("无法打开文件夹：" & ex.Message, HintType.Red)
        End Try
    End Sub

#End Region

#Region "今日人品"

    Public Shared Sub Jrrp()
        Dim Seed As String = GetDeviceId() & "|" & Date.Now.ToString("yyyy-MM-dd")
        Dim Score As Integer = StableHash(Seed) Mod 101
        Hint($"今日人品：{Score} 分 · {JrrpComment(Score)}", JrrpType(Score))
    End Sub

    Private Shared Function JrrpComment(Score As Integer) As String
        Select Case Score
            Case 100 : Return "完美的满分，今天做什么都顺。"
            Case >= 90 : Return "运气好得离谱，去买张彩票吧。"
            Case >= 75 : Return "状态不错，适合开新坑。"
            Case >= 60 : Return "正常发挥，稳一点就好。"
            Case >= 45 : Return "平平淡淡，别折腾。"
            Case >= 30 : Return "有点背，先备份再动手。"
            Case >= 10 : Return "不太行，今天就别装模组了。"
            Case Else : Return "建议原地躺平，明天再说。"
        End Select
    End Function

    Private Shared Function JrrpType(Score As Integer) As HintType
        If Score >= 60 Then Return HintType.Green
        If Score >= 30 Then Return HintType.Blue
        Return HintType.Red
    End Function

    ''' <summary>稳定的字符串散列（FNV-1a），保证同一天同一设备结果不变。</summary>
    Private Shared Function StableHash(Text As String) As Integer
        Dim H As UInteger = 2166136261UI
        For Each C As Char In Text
            H = (H Xor CUInt(AscW(C))) * 16777619UI
        Next
        Return CInt(H And 2147483647UI)
    End Function

#End Region

#Region "内存优化"

    Private Const PROCESS_QUERY_INFORMATION As Integer = &H400
    Private Const PROCESS_SET_QUOTA As Integer = &H100

    <DllImport("psapi.dll", SetLastError:=True)>
    Private Shared Function EmptyWorkingSet(hProcess As IntPtr) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function OpenProcess(dwDesiredAccess As Integer, bInheritHandle As Boolean,
                                        dwProcessId As Integer) As IntPtr
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function CloseHandle(hObject As IntPtr) As Boolean
    End Function

    ''' <summary>
    ''' 真正执行清空工作集。只应在 --memory 子进程里调用（见 Application 的启动参数处理），
    ''' 那个模式会用退出码把释放的 KB 数传回来。
    ''' </summary>
    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        Dim Done As Integer = 0, Failed As Integer = 0
        For Each P As Process In Process.GetProcesses()
            Dim Handle As IntPtr = IntPtr.Zero
            Try
                Handle = OpenProcess(PROCESS_QUERY_INFORMATION Or PROCESS_SET_QUOTA, False, P.Id)
                If Handle <> IntPtr.Zero Then
                    If EmptyWorkingSet(Handle) Then Done += 1 Else Failed += 1
                Else
                    Failed += 1
                End If
            Catch
                Failed += 1
            Finally
                If Handle <> IntPtr.Zero Then CloseHandle(Handle)
                P.Dispose()
            End Try
        Next
        Logger.Info($"内存优化：成功 {Done} 个进程，跳过 {Failed} 个（无权限或被占用）")
        If ShowHint Then Hint($"内存优化完成：处理了 {Done} 个进程", HintType.Green)
    End Sub

    ''' <summary>
    ''' 对外入口：起一个 <c>--memory</c> 子进程去做清理，再按它的退出码读出释放的 KB。
    ''' 之所以绕一层子进程，是因为 Application 里已经有对应的参数分支，且这样能把
    ''' 「清理结果」以退出码的形式安全传回（见 Application.xaml.vb 的 --memory 分支）。
    ''' </summary>
    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        RunInNewThread(
        Sub()
            Try
                Dim Info As New ProcessStartInfo(PathExe) With {
                    .Arguments = "--memory",
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Dim FreedKb As Long = 0
                Using P As Process = Process.Start(Info)
                    P.WaitForExit(60000)
                    If P.HasExited Then FreedKb = P.ExitCode
                End Using
                If ShowHint Then
                    If FreedKb > 0 Then
                        Hint($"内存优化完成，释放约 {FreedKb / 1024} MB 内存", HintType.Green)
                    ElseIf FreedKb = 0 Then
                        Hint("内存已经比较干净了，没有可释放的空间", HintType.Blue)
                    Else
                        Hint("内存优化失败（子进程返回异常）", HintType.Red)
                    End If
                End If
            Catch ex As Exception
                Logger.Warn(ex, "内存优化失败")
                If ShowHint Then Hint("内存优化失败：" & ex.Message, HintType.Red)
            End Try
        End Sub, "Memory Optimize")
    End Sub

#End Region

#Region "清理游戏垃圾"

    Public Shared Sub RubbishClear()
        Dim Targets As New List(Of String)
        '启动器自己的缓存与临时目录
        Targets.Add(PathTemp & "Cache")
        Targets.Add(PathTemp & "Temp")
        '当前实例的日志与崩溃报告（只清这两类，绝不碰存档与模组）
        Try
            Dim Selected As McInstance = McInstanceSelected
            If Selected IsNot Nothing Then
                Dim Indie As String = Selected.PathIndie
                If Not String.IsNullOrWhiteSpace(Indie) Then
                    Targets.Add(Indie & "logs")
                    Targets.Add(Indie & "crash-reports")
                End If
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取当前实例路径失败，只清理启动器缓存")
        End Try

        Dim Existing As New List(Of String)
        Dim TotalSize As Long = 0
        For Each Target In Targets
            Try
                If Directory.Exists(Target) Then
                    Existing.Add(Target)
                    TotalSize += MeasureDirectory(Target)
                End If
            Catch
            End Try
        Next
        If Existing.Count = 0 Then
            Hint("没有找到可清理的内容", HintType.Blue)
            Return
        End If

        Dim Preview As String = String.Join(vbCrLf, Existing.Select(Function(P) "  · " & P))
        Dim Confirm As Integer = 0
        RunInUiWait(Sub()
                        Confirm = MyMsgBox($"将清理以下位置（共约 {TotalSize / 1024} KB）：" & vbCrLf & vbCrLf &
                                           Preview & vbCrLf & vbCrLf &
                                           "只删除日志、崩溃报告与启动器缓存，不会动存档和模组。",
                                           "清理游戏垃圾", "清理", "取消", IsWarn:=True)
                    End Sub)
        If Confirm <> 1 Then
            Hint("已取消", HintType.Blue)
            Return
        End If

        Dim Freed As Long = 0
        Dim Removed As Integer = 0
        For Each Target In Existing
            Try
                Freed += MeasureDirectory(Target)
                Directory.Delete(Target, True)
                Removed += 1
            Catch ex As Exception
                Logger.Warn(ex, "清理目录失败：" & Target)
            End Try
        Next
        Hint($"清理完成：{Removed} 个位置，约 {Freed / 1024} KB", If(Removed > 0, HintType.Green, HintType.Red))
    End Sub

    Private Shared Function MeasureDirectory(Folder As String) As Long
        Dim Size As Long = 0
        Try
            For Each F As String In Directory.GetFiles(Folder, "*", SearchOption.AllDirectories)
                Try
                    Size += New FileInfo(F).Length
                Catch
                End Try
            Next
        Catch
        End Try
        Return Size
    End Function

#End Region

#Region "下载自定义文件"

    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        Url = If(Url, "").Trim
        If Url.Length = 0 Then
            Hint("请先填写下载地址", HintType.Red)
            Return
        End If
        If Not (Url.StartsWithF("http://", True) OrElse Url.StartsWithF("https://", True)) Then
            Hint("只支持 http:// 或 https:// 的地址", HintType.Red)
            Return
        End If

        If String.IsNullOrWhiteSpace(Folder) Then Folder = DefaultDownloadFolder()
        Folder = Folder.Trim.TrimEnd("\"c)
        If String.IsNullOrWhiteSpace(FileName) Then
            Try
                FileName = PathUtils.GetLastPart(Url)
            Catch
            End Try
        End If
        FileName = SanitizeFileName(FileName)
        If FileName.Length = 0 Then FileName = "download.bin"

        Dim Target As String = Path.Combine(Folder, FileName)
        RunInNewThread(
        Sub()
            Try
                Directory.CreateDirectory(Folder)
                Dim Task As New LoaderDownload($"下载 {FileName}", {New NetFile({Url}, Target)})
                Try
                    Task.WaitForExitTime(1800000)
                Finally
                    Task.Cancel()
                End Try
                If FileUtils.Exists(Target) Then
                    Hint($"已下载到 {Target}", HintType.Green)
                Else
                    Hint("下载没有产生文件，请检查地址", HintType.Red)
                End If
            Catch ex As Exception
                Logger.Warn(ex, "自定义下载失败")
                Hint("下载失败：" & ex.Message, HintType.Red)
            End Try
        End Sub, "Custom Download")
    End Sub

    Public Shared Function DefaultDownloadFolder() As String
        Try
            Return Paths.Base & "MyDownload\"
        Catch
            Return PathTemp & "MyDownload\"
        End Try
    End Function

    Private Shared Function SanitizeFileName(Name As String) As String
        Dim Result As New StringBuilder(If(Name, "").Trim)
        For Each C As Char In Path.GetInvalidFileNameChars()
            Result.Replace(C.ToString(), "")
        Next
        Result.Replace("/", "").Replace("\", "")
        Return Result.ToString().Trim()
    End Function

#End Region

#Region "随机文案"

    '回声洞：本地内置文案。上游原版的内容来自服务端，开源版已移除，
    '这里放自己维护的短句，需要增删直接改这个数组即可。
    Private Shared ReadOnly Caves As String() = {
        "有人在凌晨三点通关了整合包，然后在标题界面坐了一个小时。",
        "存档丢了不可怕，可怕的是你以为备份过。",
        "模组装多了不是问题，问题是你不记得装过什么。",
        "最稳定的版本永远是上一个。",
        "玩到最后发现，卡顿的不是配置，是矿车轨道太长。",
        "把整合包当抽卡游戏玩，也是一种玩法。",
        "「再玩十分钟」是一个时间单位，约等于两个小时。",
        "你以为是光影吃配置，其实是粒子效果。",
        "红石电路调通的瞬间，比通关还爽。",
        "别人的存档永远比自己的好看。",
        "别在深夜点击「生成新世界」，你会失去睡眠。",
        "最贵的不是显卡，是那些再也没打开过的存档。",
        "生存第一天：徒手砍树。第一百天：还在徒手砍树。",
        "备份是一种只有在需要时才想起的习惯。",
        "截图存了一千张，回看的次数是零。"
    }

    '启动界面的「你知道吗」提示：写成与这个启动器相关、真能帮上忙的内容。
    Private Shared ReadOnly Hints As String() = {
        "本启动器的联机节点由服务端下发，更换节点不需要更新客户端。",
        "在「设置 → 联机」里可以把节点改成自建，用自己的中继地址。",
        "更新包和联机模块都会校验哈希，下载被篡改时会直接拒绝。",
        "公告与更新信息都带数字签名，验签失败的内容不会被展示。",
        "启动器日志在 PCL\\Log1.txt，反馈问题时附上它会快很多。",
        "整合包安装失败的详细原因通常能在日志里直接找到。",
        "Java 版本不对是启动失败最常见的原因之一。",
        "游戏崩溃后可以在「更多 → 崩溃分析」里看可能的原因。",
        "内存优化会暂时降低其他程序的物理内存占用，需要管理员权限。",
        "缓存目录里堆的临时文件可以靠「清理游戏垃圾」一次性清掉。"
    }

    Private Shared ReadOnly PresetHints As String() = {
        "在这里输入你想要的页面样式，回车即可。",
        "预设写法可以参考社区里的分享，格式都是固定的键值对。",
        "不喜欢的预设随时可以右键删除，不会影响存档。"
    }

    Public Shared Function GetRandomCave() As String
        Return Caves(RandomInteger(0, Caves.Length - 1))
    End Function

    Public Shared Function GetRandomHint() As String
        Return Hints(RandomInteger(0, Hints.Length - 1))
    End Function

    Public Shared Function GetRandomPresetHint() As String
        Return PresetHints(RandomInteger(0, PresetHints.Length - 1))
    End Function

#End Region

End Class
