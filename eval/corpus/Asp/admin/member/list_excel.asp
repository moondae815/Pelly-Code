<%@ Language="VBScript" CodePage=65001 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<%
'========================================
' 관리자 > 회원 목록 엑셀 받기
'========================================
Dim rs, sql

Response.CharSet = "utf-8"
Response.ContentType = "application/vnd.ms-excel"
Response.AddHeader "Content-Disposition", "attachment; filename=members.xls"

Call OpenDb()
Set rs = Server.CreateObject("ADODB.Recordset")
sql = "SELECT CustomerId, Name, Email, CreatedAt FROM dbo.Customers ORDER BY CustomerId"
rs.Open sql, DbConn, adOpenForwardOnly, adLockReadOnly, adCmdText
%>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=utf-8">
</head>
<body>
<table border="1">
<tr>
  <th>회원 번호</th>
  <th>이름</th>
  <th>이메일</th>
  <th>가입일</th>
</tr>
<% Do While Not rs.EOF %>
<tr>
  <td><%= rs("CustomerId") %></td>
  <td><%= Server.HTMLEncode(rs("Name")) %></td>
  <td><%= rs("Email") %></td>
  <td><%= FormatDateTime(rs("CreatedAt"), vbShortDate) %></td>
</tr>
<%
    rs.MoveNext
Loop
rs.Close
Set rs = Nothing
Call CloseDb()
%>
</table>
</body>
</html>
