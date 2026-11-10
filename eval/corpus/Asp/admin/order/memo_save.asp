<%@ Language="VBScript" CodePage=949 %>
<% Option Explicit %>
<%
Dim orderNo, memoText
orderNo = Trim(Request.Form("orderNo"))
memoText = Trim(Request.Form("memoText"))
If orderNo = "" Then
    Response.Write "missing order"
    Response.End
End If
Response.Write "saved " & Server.HTMLEncode(orderNo)
%>
