<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<!--#include virtual="/inc/func_order.inc"-->
<%
'========================================
' 관리자 > 고객별 주문 요약
'========================================

' 고객의 주문 목록을 읽어 온다
Function LoadOrderRows(customerId)
    Dim rows
    Set rows = DbConn.Execute("exec dbo.usp_OrderList @CustomerId = " & CLng(customerId))
    Set LoadOrderRows = rows
End Function

' 한 줄에 보일 문구를 만든다
Function FormatRow(orderId, status, amount)
    Dim text
    text = orderId & " / " & StatusName(status) & " / " & amount
    FormatRow = text
End Function

Dim custId, rs, line
custId = Trim(Request("custId"))
If custId = "" Or Not IsNumeric(custId) Then custId = 0
Call OpenDb()
Set rs = LoadOrderRows(custId)
%>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=euc-kr">
<title>주문 요약</title>
</head>
<body>
<h1>주문 요약</h1>
<ul>
<% Do While Not rs.EOF
    line = FormatRow(rs("OrderId"), rs("Status"), rs("TotalAmount")) %>
    <li><%= Server.HTMLEncode(line) %></li>
<%  rs.MoveNext
Loop %>
</ul>
<% Call CloseDb() %>
</body>
</html>
