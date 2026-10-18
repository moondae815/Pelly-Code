SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- =============================================
-- 주문 처리 프로시저 모음 (한 파일에 여러 객체)
-- =============================================
-- 배송 시작: TRY/CATCH + 트랜잭션 사례
CREATE PROCEDURE dbo.usp_ShipOrder
    @OrderId    int,
    @Carrier    nvarchar(30),
    @TrackingNo varchar(30)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRAN;

        UPDATE dbo.Orders
        SET Status = 2,
            ShippedAt = SYSDATETIME()
        WHERE OrderId = @OrderId
          AND Status = 1;

        IF @@ROWCOUNT = 0
        BEGIN
            RAISERROR(N'결제 완료 상태의 주문만 배송 처리할 수 있습니다.', 16, 1);
        END

        INSERT INTO dbo.Shipments (OrderId, Carrier, TrackingNo, ShippedAt)
        VALUES (@OrderId, @Carrier, @TrackingNo, SYSDATETIME());

        INSERT INTO dbo.OrderHistories (OrderId, Message, CreatedAt)
        VALUES (@OrderId, N'배송 시작: ' + @Carrier + N' ' + @TrackingNo, SYSDATETIME());

        COMMIT TRAN;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRAN;
        THROW;
    END CATCH
END
GO

-- 결제 기한이 지난 결제 전 주문 자동 취소: 커서 사례
CREATE PROCEDURE dbo.usp_ExpirePendingOrders
    @Days int = 3
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Cutoff datetime2(0) = DATEADD(DAY, -@Days, SYSDATETIME());
    DECLARE @OrderId int;
    DECLARE @Expired int = 0;

    DECLARE pending_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT OrderId
        FROM dbo.Orders
        WHERE Status = 0
          AND OrderedAt < @Cutoff;

    OPEN pending_cursor;
    FETCH NEXT FROM pending_cursor INTO @OrderId;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        UPDATE dbo.Orders
        SET Status = 9,
            CancelledAt = SYSDATETIME(),
            CancelReason = N'결제 기한 만료'
        WHERE OrderId = @OrderId
          AND Status = 0;

        SET @Expired = @Expired + @@ROWCOUNT;

        FETCH NEXT FROM pending_cursor INTO @OrderId;
    END

    CLOSE pending_cursor;
    DEALLOCATE pending_cursor;

    SELECT @Expired AS ExpiredCount;
END
GO
