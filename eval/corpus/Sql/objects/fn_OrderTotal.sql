SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 주문 상품 합계 (배송비 제외)
CREATE FUNCTION dbo.fn_OrderTotal (@OrderId int)
RETURNS decimal(18,2)
AS
BEGIN
    DECLARE @Total decimal(18,2);

    SELECT @Total = SUM(Quantity * UnitPrice)
    FROM dbo.OrderItems
    WHERE OrderId = @OrderId;

    RETURN ISNULL(@Total, 0);
END
GO
