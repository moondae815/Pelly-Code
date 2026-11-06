<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=euc-kr">
<title>옛 화면</title>
<script language="JavaScript">
<!--
function fnOldOpen(url) {
    window.open(url, "popup", "width=400,height=300");
}
//-->
</script>
<!--
<script type="text/javascript">
function fnDisabled() {
    alert("사용하지 않는 함수입니다.");
}
</script>
-->
<script language="VBScript">
Sub fnOldHello()
    MsgBox "안녕하세요"
End Sub
</script>
</head>
<body>
<a href="javascript:fnOldOpen('/help/guide.htm')">도움말</a>
</body>
</html>
