<%
'========================================
' 보고서 공통 함수
'========================================

' 저장 프로시저를 이름으로 실행한다 (spName: 스키마를 포함한 프로시저 이름, args: 인수 배열)
Function ExecSP(spName, args)
    Dim cmd, i
    Set cmd = Server.CreateObject("ADODB.Command")
    Set cmd.ActiveConnection = DbConn
    cmd.CommandType = adCmdStoredProc
    cmd.CommandText = spName
    For i = 0 To UBound(args)
        cmd.Parameters.Append cmd.CreateParameter("@p" & i, adVarChar, adParamInput, 50, args(i))
    Next
    Set ExecSP = cmd.Execute
End Function
%>
