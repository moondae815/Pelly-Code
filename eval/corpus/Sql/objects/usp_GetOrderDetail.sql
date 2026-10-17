SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 주문 상세 (주문, 상품, 배송 3개 결과 집합)
CREATE PROCEDURE dbo.usp_GetOrderDetail
    @OrderId int
AS
BEGIN
    SET NOCOUNT ON;

    SELECT o.OrderId, o.CustomerId, c.Name AS CustomerName, o.Status, o.TotalAmount, o.ShippingFee,
           o.DiscountAmount, o.PointUsed, o.OrderedAt, o.PaidAt, o.ShippedAt, o.DeliveredAt
    FROM dbo.Orders o
    JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
    WHERE o.OrderId = @OrderId;

    SELECT OrderItemId, ProductId, ProductName, Quantity, UnitPrice, Quantity * UnitPrice AS Amount
    FROM dbo.OrderItems
    WHERE OrderId = @OrderId
    ORDER BY OrderItemId;

    SELECT Carrier, TrackingNo, ShippedAt, DeliveredAt
    FROM dbo.Shipments
    WHERE OrderId = @OrderId;
END
GO
