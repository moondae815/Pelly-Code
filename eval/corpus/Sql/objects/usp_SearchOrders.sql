SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 주문 검색
CREATE PROCEDURE dbo.usp_SearchOrders
    @CustomerId int = NULL,
    @Status     tinyint = NULL,
    @From       datetime2(0) = NULL,
    @To         datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT o.OrderId, o.CustomerId, c.Name AS CustomerName, o.Status, o.TotalAmount, o.ShippingFee, o.OrderedAt
    FROM dbo.Orders o
    JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
    WHERE (@CustomerId IS NULL OR o.CustomerId = @CustomerId)
      AND (@Status IS NULL OR o.Status = @Status)
      AND (@From IS NULL OR o.OrderedAt >= @From)
      AND (@To IS NULL OR o.OrderedAt < @To)
    ORDER BY o.OrderedAt DESC;
END
GO
