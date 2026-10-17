SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- =============================================
-- 정산 프로시저 모음 (한 파일에 여러 객체)
-- =============================================
CREATE PROCEDURE dbo.usp_SettleDaily
    @BusinessDate date
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM dbo.Settlements WHERE BusinessDate = @BusinessDate)
        RETURN;

    INSERT INTO dbo.Settlements (BusinessDate, SellerId, Category, OrderCount, SalesAmount, ShippingAmount, CommissionAmount, PayoutAmount, CreatedAt)
    SELECT @BusinessDate, o.SellerId, NULL, COUNT(*),
           SUM(o.TotalAmount - o.ShippingFee),
           SUM(o.ShippingFee),
           ROUND(SUM(o.TotalAmount - o.ShippingFee) * MAX(ISNULL(s.CommissionRate, 0.12)), 0),
           SUM(o.TotalAmount) - ROUND(SUM(o.TotalAmount - o.ShippingFee) * MAX(ISNULL(s.CommissionRate, 0.12)), 0),
           SYSDATETIME()
    FROM dbo.Orders o
    JOIN dbo.Sellers s ON s.SellerId = o.SellerId
    WHERE o.Status = 3
      AND o.DeliveredAt >= @BusinessDate
      AND o.DeliveredAt < DATEADD(DAY, 1, @BusinessDate)
    GROUP BY o.SellerId;
END
GO

-- 월 정산 요약
CREATE PROCEDURE dbo.usp_SettleMonthly
    @Year  int,
    @Month int
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From date = DATEFROMPARTS(@Year, @Month, 1);
    DECLARE @To date = DATEADD(MONTH, 1, @From);

    SELECT st.SellerId, se.Name AS SellerName,
           SUM(st.OrderCount) AS OrderCount,
           SUM(st.SalesAmount) AS SalesAmount,
           SUM(st.CommissionAmount) AS CommissionAmount,
           SUM(st.PayoutAmount) AS PayoutAmount
    FROM dbo.Settlements st
    JOIN dbo.Sellers se ON se.SellerId = st.SellerId
    WHERE st.BusinessDate >= @From AND st.BusinessDate < @To
    GROUP BY st.SellerId, se.Name
    ORDER BY SalesAmount DESC;
END
GO

GRANT EXECUTE ON dbo.usp_SettleDaily TO public
GO
GRANT EXECUTE ON dbo.usp_SettleMonthly TO public
GO
