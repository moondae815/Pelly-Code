<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<%
Dim orderNo
orderNo = Request("orderNo")
%>
<html>
<body>
<form name="frm" method="post">
<input type="hidden" name="orderNo" value="<%= Server.HTMLEncode(orderNo) %>">
<textarea name="memoText" rows="4" cols="60"></textarea>
<input type="button" value="save" onclick="fnSaveMemo()">
</form>
<script type="text/javascript">
function fnSaveMemo() {
    frm.action = "memo_save.asp";
    frm.submit();
}
</script>
</body>
</html>
