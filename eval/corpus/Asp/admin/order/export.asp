<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<%
'========================================
' 관리자 > 주문 내려받기 (기간별)
'========================================
Dim fromDate, toDate, cmd, rs

fromDate = Trim(Request("fromDate"))
toDate = Trim(Request("toDate"))

Call OpenDb()
Set cmd = Server.CreateObject("ADODB.Command")
Set cmd.ActiveConnection = DbConn
cmd.CommandText = "{call dbo.usp_OrderList(?, ?, ?)}"
cmd.Parameters.Append cmd.CreateParameter("@c", adInteger, adParamInput, , Null)
cmd.Parameters.Append cmd.CreateParameter("@f", adVarChar, adParamInput, 20, fromDate)
cmd.Parameters.Append cmd.CreateParameter("@t", adVarChar, adParamInput, 20, toDate)
Set rs = cmd.Execute
%>
<html>
<body>
<p><%= rs.State %></p>
<% Call CloseDb() %>
</body>
</html>
