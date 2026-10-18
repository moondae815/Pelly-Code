SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 판매자 수수료율 변경
CREATE PROCEDURE dbo.usp_SetSellerCommission
    @SellerId       int,
    @CommissionRate decimal(5,4)
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Sellers WHERE SellerId = @SellerId)
    BEGIN
        RAISERROR(N'판매자를 찾을 수 없습니다.', 16, 1);
        RETURN;
    END

    UPDATE dbo.Sellers
    SET CommissionRate = @CommissionRate
    WHERE SellerId = @SellerId;

    SELECT SellerId, Name, CommissionRate
    FROM dbo.Sellers
    WHERE SellerId = @SellerId;
END
GO
