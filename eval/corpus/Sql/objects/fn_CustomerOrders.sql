SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 고객별 주문 목록 (인라인 테이블 반환 함수)
CREATE FUNCTION dbo.fn_CustomerOrders (@CustomerId int)
RETURNS TABLE
AS
RETURN
(
    SELECT o.OrderId, o.Status, o.TotalAmount, o.ShippingFee, o.OrderedAt,
           (SELECT COUNT(*) FROM dbo.OrderItems i WHERE i.OrderId = o.OrderId) AS ItemCount
    FROM dbo.Orders o
    WHERE o.CustomerId = @CustomerId
);
GO
