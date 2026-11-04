<%
'========================================
' 공통 함수
'========================================

' SQL 문자열 안에 넣을 값의 작은따옴표를 이스케이프한다
Function SafeSQL(s)
    If IsNull(s) Then
        SafeSQL = ""
    Else
        SafeSQL = Replace(Trim(CStr(s)), "'", "''")
    End If
End Function

' 값이 Null이거나 비어 있으면 기본값을 돌려준다
Function NVL(value, defaultValue)
    If IsNull(value) Then
        NVL = defaultValue
    ElseIf Trim(value & "") = "" Then
        NVL = defaultValue
    Else
        NVL = value
    End If
End Function

' <option> 목록을 쓴다. 값과 이름은 "|"로 구분한다
Sub WriteSelectOptions(values, labels, selected)
    Dim v, l, i
    v = Split(values, "|")
    l = Split(labels, "|")
    For i = 0 To UBound(v)
        Response.Write "<option value=""" & v(i) & """"
        If CStr(v(i)) = CStr(selected) Then Response.Write " selected"
        Response.Write ">" & Server.HTMLEncode(l(i)) & "</option>" & vbCrLf
    Next
End Sub

' 경고창을 띄우고 이전 화면으로 돌아간다
Sub AlertBack(message)
    Response.Write "<script type=""text/javascript"">alert('" & Replace(message, "'", "\'") & "');history.back();</script>"
    Response.End
End Sub
%>
