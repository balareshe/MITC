Public Class PageSetupLicense

    Private Sub PageSetupLicense_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        PanBack.ScrollToHome()
        RefreshLicenseInfo()
    End Sub

    Public Sub Reload()
        RefreshLicenseInfo()
    End Sub

    ''' <summary>
    ''' 读取本地授权状态并刷新界面。全部为本地数据，不发网络请求。
    ''' </summary>
    Private Sub RefreshLicenseInfo()
        Try
            Dim Key As String = ""
            Try
                Key = Settings.Get(Of String)("SystemLicenseKey").Trim()
            Catch
            End Try

            TextLicenseDevice.Text = ModSecret.GetDeviceId()

            '有效期（空 = 永久）
            Dim ExpRaw As String = ""
            Try
                ExpRaw = Settings.Get(Of String)("SystemLicenseExpires").Trim()
            Catch
            End Try
            Dim ExpValue As Date
            Dim HasExpiry As Boolean = ExpRaw <> "" AndAlso Date.TryParse(ExpRaw, ExpValue)
            Dim IsExpired As Boolean = HasExpiry AndAlso ExpValue <= Date.Now

            If Key = "" Then
                TextLicenseState.Text = "未授权"
                TextLicenseState.Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
                TextLicenseKey.Text = "—"
            ElseIf IsExpired Then
                TextLicenseState.Text = "已过期"
                TextLicenseState.Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
            Else
                '脱敏显示：保留前缀字母段与末 4 位
                Dim Plain As String = Key.Replace("-", "")
                If Plain.Length >= 12 Then
                    Dim Head As String = If(Plain.StartsWith("MITC"), "MITC",
                                            Plain.Substring(0, Math.Min(4, Plain.Length)))
                    TextLicenseKey.Text = Head & "-****-****-" & Plain.Substring(Plain.Length - 4)
                Else
                    TextLicenseKey.Text = "********"
                End If
                If ModSecret.LicenseWithinGrace() Then
                    TextLicenseState.Text = "已授权（离线宽限内）"
                    TextLicenseState.Foreground = New SolidColorBrush(Color.FromRgb(52, 148, 84))
                Else
                    TextLicenseState.Text = "已授权（需要重新校验）"
                    TextLicenseState.Foreground = New SolidColorBrush(Color.FromRgb(200, 130, 20))
                End If
            End If

            '显示有效期
            If HasExpiry Then
                If IsExpired Then
                    TextLicenseExpires.Text = ExpValue.ToString("yyyy-MM-dd HH:mm:ss") & "（已过期）"
                    TextLicenseExpires.Foreground = New SolidColorBrush(Color.FromRgb(193, 43, 43))
                Else
                    Dim LeftDays As Double = (ExpValue - Date.Now).TotalDays
                    Dim Remain As String = If(LeftDays >= 1, CInt(Math.Floor(LeftDays)).ToString() & " 天", "不足 1 天")
                    TextLicenseExpires.Text = ExpValue.ToString("yyyy-MM-dd HH:mm:ss") & "（剩余 " & Remain & "）"
                    TextLicenseExpires.Foreground = New SolidColorBrush(Color.FromRgb(200, 130, 20))
                End If
            Else
                TextLicenseExpires.Text = "永久"
                TextLicenseExpires.Foreground = New SolidColorBrush(Color.FromRgb(52, 148, 84))
            End If

            Dim Raw As String = ""
            Try
                Raw = Settings.Get(Of String)("SystemLicenseLastOk").Trim()
            Catch
            End Try
            Dim Unix As Long = 0
            If Raw <> "" AndAlso Long.TryParse(Raw, Unix) AndAlso Unix > 0 Then
                Dim Moment As DateTimeOffset = DateTimeOffset.FromUnixTimeSeconds(Unix).ToLocalTime()
                Dim Age As TimeSpan = DateTimeOffset.Now - Moment
                Dim Ago As String
                If Age.TotalMinutes < 1 Then
                    Ago = "刚刚"
                ElseIf Age.TotalHours < 1 Then
                    Ago = CInt(Age.TotalMinutes) & " 分钟前"
                ElseIf Age.TotalDays < 1 Then
                    Ago = CInt(Age.TotalHours) & " 小时前"
                Else
                    Ago = CInt(Age.TotalDays) & " 天前"
                End If
                TextLicenseLastOk.Text = Moment.ToString("yyyy-MM-dd HH:mm") & "（" & Ago & "）"
            Else
                TextLicenseLastOk.Text = "尚未校验"
            End If
        Catch ex As Exception
            Logger.Warn(ex, "刷新授权信息失败")
        End Try
    End Sub

    '重新校验（后台线程请求，避免 UI 线程网络请求限制）
    Private Sub BtnLicenseRecheck_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLicenseRecheck.Click
        Dim Key As String = ""
        Try
            Key = Settings.Get(Of String)("SystemLicenseKey").Trim()
        Catch
        End Try
        If Key = "" Then
            Hint("当前没有本地授权码，请先点击「更换授权码」")
            Return
        End If
        BtnLicenseRecheck.IsEnabled = False
        Dim Bg As New Threading.Thread(
            Sub()
                Dim Msg As String = ""
                Dim Result As Integer = 0
                Try
                    Result = ModSecret.LicenseVerify(Key, Msg)
                Catch ex As Exception
                    Logger.Warn(ex, "重新校验授权失败")
                End Try
                BtnLicenseRecheck.Dispatcher.BeginInvoke(New Action(
                    Sub()
                        BtnLicenseRecheck.IsEnabled = True
                        RefreshLicenseInfo()
                        Select Case Result
                            Case 1
                                Hint("授权校验通过", HintType.Green, False)
                            Case -1
                                Hint("授权被拒绝：" & Msg, HintType.Red, False)
                            Case Else
                                Hint("无法连接授权服务器，请检查网络", HintType.Red, False)
                        End Select
                    End Sub))
            End Sub)
        Bg.IsBackground = True
        Bg.Start()
    End Sub

    '复制授权码
    Private Sub BtnLicenseCopy_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLicenseCopy.Click
        Dim Key As String = ""
        Try
            Key = Settings.Get(Of String)("SystemLicenseKey").Trim()
        Catch
        End Try
        If Key = "" Then
            Hint("当前没有本地授权码")
            Return
        End If
        Try
            Clipboard.SetText(Key)
            Hint("授权码已复制", HintType.Green, False)
        Catch ex As Exception
            Logger.Warn(ex, "复制授权码失败")
            Hint("复制失败，请手动记录")
        End Try
    End Sub

    '更换授权码
    Private Sub BtnLicenseChange_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnLicenseChange.Click
        If ModSecret.ShowLicenseDialog("") Then
            RefreshLicenseInfo()
            Hint("授权已更新", HintType.Green, False)
        Else
            RefreshLicenseInfo()
        End If
    End Sub

End Class
