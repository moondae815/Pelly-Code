<%@ Language="VBScript" %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<%
'----------------------------------------
' Admin > cancel order
'----------------------------------------
Dim orderId, reason, cmd

orderId = Trim(Request.Form("orderId"))
reason = Trim(Request.Form("reason"))
If orderId = "" Or Not IsNumeric(orderId) Then
    Call AlertBack("Invalid order number.")
End If
If reason = "" Then reason = "admin"

Call OpenDb()

Set cmd = Server.CreateObject("ADODB.Command")
Set cmd.ActiveConnection = DbConn
cmd.CommandText = "dbo.usp_CancelOrder"
cmd.CommandType = adCmdStoredProc
cmd.Parameters.Append cmd.CreateParameter("@OrderId", adInteger, adParamInput, , CLng(orderId))
cmd.Parameters.Append cmd.CreateParameter("@Reason", adVarWChar, adParamInput, 200, reason)
cmd.Execute , , adExecuteNoRecords

Set cmd = Nothing
Call CloseDb()

Response.Write "<script type=""text/javascript"">alert('Order cancelled.');location.href='list.asp';</script>"
%>