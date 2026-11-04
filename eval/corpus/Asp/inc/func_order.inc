<%
'========================================
' 주문 관련 함수
'========================================

' 결제 금액 = 상품 금액 + 배송비 - 할인 - 사용 포인트
Function GetOrderAmount(totalAmount, shippingFee, discountAmount, pointUsed)
    Dim amount
    amount = CDbl(NVL(totalAmount, 0)) + CDbl(NVL(shippingFee, 0))
    amount = amount - CDbl(NVL(discountAmount, 0)) - CDbl(NVL(pointUsed, 0))
    If amount < 0 Then amount = 0
    GetOrderAmount = amount
End Function

' 주문 상태 코드를 화면에 보일 이름으로 바꾼다
Function StatusName(code)
    Select Case CInt(NVL(code, -1))
        Case 0
            StatusName = "주문 접수"
        Case 1
            StatusName = "결제 완료"
        Case 2
            StatusName = "배송 중"
        Case 3
            StatusName = "배송 완료"
        Case 9
            StatusName = "취소"
        Case Else
            StatusName = "알 수 없음"
    End Select
End Function
%>
