SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 판매자별 정산 미리보기 - #temp 테이블 사례
CREATE PROCEDURE dbo.usp_BuildSettlement
    @BusinessDate date
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #Sales (
        SellerId    int NOT NULL,
        OrderCount  int NOT NULL,
        SalesAmount decimal(18,2) NOT NULL
    );

    INSERT INTO #Sales (SellerId, OrderCount, SalesAmount)
    SELECT o.SellerId, COUNT(*), SUM(o.TotalAmount - o.ShippingFee)
    FROM dbo.Orders o
    WHERE o.Status = 3
      AND o.DeliveredAt >= @BusinessDate
      AND o.DeliveredAt < DATEADD(DAY, 1, @BusinessDate)
    GROUP BY o.SellerId;

    SELECT s.SellerId, se.Name AS SellerName, s.OrderCount, s.SalesAmount,
           ROUND(s.SalesAmount * ISNULL(se.CommissionRate, 0.12), 0) AS CommissionAmount
    FROM #Sales s
    JOIN dbo.Sellers se ON se.SellerId = s.SellerId
    ORDER BY s.SalesAmount DESC;

    DROP TABLE #Sales;
END
GO
