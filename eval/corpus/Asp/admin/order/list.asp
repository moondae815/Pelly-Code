<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<!--#include virtual="/inc/func_order.inc"-->
<%
'========================================
' 관리자 > 주문 목록
'========================================
Dim custId, fromDate, toDate, status
Dim page, pageSize, pageCount, recordCount, i
Dim sql, rs, rowAmount, pageTotal

custId = Trim(Request("custId"))
fromDate = Trim(Request("fromDate"))
toDate = Trim(Request("toDate"))
status = Trim(Request("status"))
page = Trim(Request("page"))
If page = "" Or Not IsNumeric(page) Then page = 1
page = CInt(page)
pageSize = 20

' 검색 조건
sql = "exec dbo.usp_SearchOrders @CustomerId = "
If custId <> "" And IsNumeric(custId) Then
    sql = sql & CLng(custId)
Else
    sql = sql & "NULL"
End If
If fromDate <> "" Then
    sql = sql & ", @From = '" & SafeSQL(fromDate) & "'"
End If
If toDate <> "" Then
    sql = sql & ", @To = '" & Request("toDate") & "'"
End If
' 검색 조건 끝

Call OpenDb()
Set rs = Server.CreateObject("ADODB.Recordset")
rs.CursorLocation = adUseClient
rs.Open sql, DbConn, adOpenStatic, adLockReadOnly, adCmdText

recordCount = rs.RecordCount
pageCount = 0
If Not rs.EOF Then
    rs.PageSize = pageSize
    pageCount = rs.PageCount
    If page > pageCount Then page = pageCount
    rs.AbsolutePage = page
End If
pageTotal = 0
%>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=euc-kr">
<title>주문 목록</title>
</head>
<body>
<div class="wrap">
<h2>주문 목록</h2>
<form method="get" action="list.asp">
고객 번호 <input type="text" name="custId" value="<%= Server.HTMLEncode(custId) %>" size="8">
기간 <input type="text" name="fromDate" value="<%= Server.HTMLEncode(fromDate) %>" size="10"> ~
<input type="text" name="toDate" value="<%= Server.HTMLEncode(toDate) %>" size="10">
상태 <select name="status">
<option value="">전체</option>
<% Call WriteSelectOptions("0|1|2|3|9", "주문 접수|결제 완료|배송 중|배송 완료|취소", status) %>
</select>
<input type="submit" value="검색">
</form>
<div class="list">
<table border="1" cellpadding="4">
<tr>
  <th>주문 번호</th><th>고객</th><th>상태</th><th>주문 금액</th><th>주문일</th>
</tr>
<% If rs.EOF Then %>
<tr><td colspan="5">검색된 주문이 없습니다.</td></tr>
<% Else %>
<%
    i = 0
    Do While Not rs.EOF And i < pageSize
        rowAmount = CDbl(rs("TotalAmount")) + CDbl(rs("ShippingFee"))
        pageTotal = pageTotal + rowAmount
%>
<tr>
  <td><a href="edit.asp?orderId=<%= rs("OrderId") %>"><%= rs("OrderId") %></a></td>
  <td><%= Server.HTMLEncode(rs("CustomerName")) %></td>
  <td><%= StatusName(rs("Status")) %></td>
  <td align="right"><%= FormatNumber(rowAmount, 0) %>원</td>
  <td><%= FormatDateTime(rs("OrderedAt"), vbShortDate) %></td>
</tr>
<%
        i = i + 1
        rs.MoveNext
    Loop
%>
<% End If %>
</table>
<p>이 페이지 합계: <%= FormatNumber(pageTotal, 0) %>원 (전체 <%= recordCount %>건)</p>
<% If pageCount > 1 Then %>
<p class="paging">
<% For i = 1 To pageCount %>
<% If i = page Then %><b><%= i %></b><% Else %><a href="list.asp?page=<%= i %>&amp;custId=<%= Server.URLEncode(custId) %>&amp;fromDate=<%= Server.URLEncode(fromDate) %>&amp;toDate=<%= Server.URLEncode(toDate) %>&amp;status=<%= Server.URLEncode(status) %>"><%= i %></a><% End If %>
<% Next %>
</p>
<% End If %>
</div>
</body>
</html>
<%
rs.Close
Set rs = Nothing
Call CloseDb()
%>
