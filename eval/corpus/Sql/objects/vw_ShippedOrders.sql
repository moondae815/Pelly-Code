SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 배송 중 주문
CREATE VIEW dbo.vw_ShippedOrders
AS
SELECT o.OrderId, o.CustomerId, c.Name AS CustomerName, s.Carrier, s.TrackingNo, o.ShippedAt
FROM dbo.Orders o
JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
LEFT JOIN dbo.Shipments s ON s.OrderId = o.OrderId
WHERE o.Status = 2;
GO
