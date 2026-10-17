SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
-- 주문 검색 (동적 정렬) - 동적 SQL 사례
CREATE PROCEDURE dbo.usp_SearchOrdersDynamic
    @CustomerId int = NULL,
    @SortColumn sysname = N'OrderedAt',
    @SortDesc   bit = 1
AS
BEGIN
    SET NOCOUNT ON;

    IF @SortColumn NOT IN (N'OrderedAt', N'TotalAmount', N'OrderId')
        SET @SortColumn = N'OrderedAt';

    DECLARE @sql nvarchar(max) = N'
SELECT o.OrderId, o.CustomerId, o.Status, o.TotalAmount, o.OrderedAt
FROM dbo.Orders o
WHERE (@CustomerId IS NULL OR o.CustomerId = @CustomerId)
ORDER BY o.' + QUOTENAME(@SortColumn) + CASE WHEN @SortDesc = 1 THEN N' DESC' ELSE N' ASC' END;

    EXEC sp_executesql @sql, N'@CustomerId int', @CustomerId = @CustomerId;
END
GO
