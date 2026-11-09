SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 주문 목록 (고객·기간별)
CREATE PROCEDURE dbo.usp_OrderList
    @CustomerId int = NULL,
    @From       datetime2(0) = NULL,
    @To         datetime2(0) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT o.OrderId, o.CustomerId, o.Status, o.TotalAmount, o.OrderedAt
    FROM dbo.Orders o
    WHERE (@CustomerId IS NULL OR o.CustomerId = @CustomerId)
      AND (@From IS NULL OR o.OrderedAt >= @From)
      AND (@To IS NULL OR o.OrderedAt < @To)
    ORDER BY o.OrderedAt DESC;
END
GO
