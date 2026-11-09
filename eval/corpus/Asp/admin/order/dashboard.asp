<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<!--#include virtual="/inc/func_report.inc"-->
<%
'========================================
' 관리자 > 주문 현황판
'========================================
Dim rs, total

Call OpenDb()
Set rs = ExecSP("dbo.usp_OrderList", Array(0, "", ""))
total = 0
Do While Not rs.EOF
    total = total + 1
    rs.MoveNext
Loop
%>
<html>
<body>
<p>주문 <%= total %>건</p>
<% Call CloseDb() %>
</body>
</html>
