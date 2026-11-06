<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<!--#include virtual="/inc/adovbs.inc"-->
<!--#include virtual="/inc/dbconn.asp"-->
<!--#include virtual="/inc/func_common.inc"-->
<!--#include virtual="/inc/func_order.inc"-->
<%
'========================================
' 관리자 > 주문 수정
'========================================
Dim orderId, cmd, rs
Dim customerName, status, totalAmount, shippingFee, discountAmount, pointUsed, orderedAt

orderId = Trim(Request("orderId"))
If orderId = "" Or Not IsNumeric(orderId) Then
    Call AlertBack("주문 번호가 올바르지 않습니다.")
End If

Call OpenDb()

Set cmd = Server.CreateObject("ADODB.Command")
Set cmd.ActiveConnection = DbConn
cmd.CommandText = "dbo.usp_GetOrderDetail"
cmd.CommandType = adCmdStoredProc
cmd.Parameters.Append cmd.CreateParameter("@OrderId", adInteger, adParamInput, , CLng(orderId))
Set rs = cmd.Execute

If rs.EOF Then
    rs.Close
    Call CloseDb()
    Call AlertBack("주문을 찾을 수 없습니다.")
End If

customerName = rs("CustomerName")
status = rs("Status")
totalAmount = rs("TotalAmount")
shippingFee = rs("ShippingFee")
discountAmount = rs("DiscountAmount")
pointUsed = rs("PointUsed")
orderedAt = rs("OrderedAt")

rs.Close
Set rs = Nothing
Set cmd = Nothing
Call CloseDb()
%>
<html>
<head>
<meta http-equiv="Content-Type" content="text/html; charset=euc-kr">
<title>주문 수정</title>
</head>
<body>
<h2>주문 수정 - <%= orderId %></h2>
<form method="post" action="save.asp">
<input type="hidden" name="orderId" value="<%= orderId %>">
<table border="1" cellpadding="4">
<tr><th>고객</th><td><%= Server.HTMLEncode(customerName) %></td></tr>
<tr><th>상태</th><td><%= StatusName(status) %></td></tr>
<tr><th>결제 금액</th><td><%= FormatNumber(GetOrderAmount(totalAmount, shippingFee, discountAmount, pointUsed), 0) %>원</td></tr>
<tr><th>주문일</th><td><%= orderedAt %></td></tr>
<% If CInt(status) < 2 Then %>
<tr><th>취소 사유</th><td><input type="text" name="reason" size="40" maxlength="200"></td></tr>
<% End If %>
</table>
<% If CInt(status) < 2 Then %>
<input type="submit" value="주문 취소">
<% Else %>
<p>배송이 시작된 주문은 취소할 수 없습니다.</p>
<% End If %>
<a href="list.asp">목록</a>
</form>
<script type="text/javascript">
function fnCancelOrder() {
<% If CInt(status) < 2 Then %>
    if (!confirm("주문을 취소하시겠습니까?")) {
        return false;
    }
    return true;
<% Else %>
    alert("배송이 시작된 주문은 취소할 수 없습니다.");
    return false;
<% End If %>
}
</script>
<script type="text/javascript">
function fnGoList() {
    location.href = "list.asp";
}
</script>
</body>
</html>
