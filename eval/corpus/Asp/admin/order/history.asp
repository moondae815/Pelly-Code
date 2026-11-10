<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<%
Dim no
no = Request("no")
%>
<html>
<head>
<script type="text/javascript" src="/js/order/history.js"></script>
</head>
<body>
<input type="button" value="history" onclick="fnHistory(1)">
<div id="historyArea"></div>
<script type="text/javascript">
var gNo = <%= CLng(no) %>;
function goHistory(n) {
    fnHistory(n);
}
</script>
</body>
</html>
