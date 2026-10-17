SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- =============================================
-- 주문 취소
-- 작성: 2017-05 / 수정: 2021-08 취소 이력 추가
-- =============================================
CREATE PROCEDURE dbo.usp_CancelOrder
    @OrderId int,
    @Reason  nvarchar(200)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Status tinyint;

    SELECT @Status = Status
    FROM dbo.Orders
    WHERE OrderId = @OrderId;

    IF @Status IS NULL
    BEGIN
        RAISERROR(N'주문을 찾을 수 없습니다.', 16, 1);
        RETURN;
    END

    IF @Status = 9
    BEGIN
        -- 이미 취소된 주문
        RETURN;
    END

    IF @Status = 3
    BEGIN
        RAISERROR(N'배송 완료된 주문은 취소할 수 없습니다.', 16, 1);
        RETURN;
    END

    BEGIN TRAN;

    UPDATE dbo.Orders
    SET Status = 9,
        CancelledAt = SYSDATETIME(),
        CancelReason = @Reason
    WHERE OrderId = @OrderId;

    INSERT INTO dbo.OrderHistories (OrderId, Message, CreatedAt)
    VALUES (@OrderId, N'주문 취소: ' + ISNULL(@Reason, N''), SYSDATETIME());

    COMMIT TRAN;
END
GO
