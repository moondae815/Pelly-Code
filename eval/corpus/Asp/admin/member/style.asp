<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<%
'========================================
' 관리자 > 회원 목록 화면 스타일
'========================================
Dim bgColor
bgColor = "#f5f5f5"
%>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=euc-kr">
<title>회원 목록</title>
<style type="text/css">
body { margin: 0; background: <%= bgColor %>; font-size: 12px; }
.member_tbl { width: 100%; border-collapse: collapse; *zoom: 1; }
.member_tbl th { background: #eeeeee; text-align: center; }
.member_tbl td { padding: 4px; border-bottom: 1px solid #dddddd; }
</style>
</head>
<body>
<h2>회원 목록</h2>
<table class="member_tbl">
<tr><th>아이디</th><th>이름</th></tr>
<tr><td>hong</td><td>홍길동</td></tr>
</table>
</body>
</html>
