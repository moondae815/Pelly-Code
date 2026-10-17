-- 샘플 데이터
SET NOCOUNT ON
GO

INSERT INTO dbo.Customers (Name, Email, Grade) VALUES
    (N'김민수', 'minsu@example.com', 1),
    (N'이서연', 'seoyeon@example.com', 2),
    (N'박지훈', 'jihoon@example.com', 1),
    (N'최유진', NULL, 3)
GO

INSERT INTO dbo.Sellers (Name, CommissionRate) VALUES
    (N'생활잡화 상점', 0.1200),
    (N'주방용품 마켓', 0.1000),
    (N'작은 공방', NULL)
GO

INSERT INTO dbo.Products (SellerId, Name, Category, Price) VALUES
    (1, N'머그컵', N'주방', 10000),
    (1, N'컵받침', N'주방', 5000),
    (2, N'프라이팬', N'주방', 32000),
    (2, N'행주 세트', N'생활', 6000),
    (3, N'원목 트레이', N'인테리어', 28000)
GO

INSERT INTO dbo.Orders (CustomerId, SellerId, Status, TotalAmount, ShippingFee, OrderedAt, PaidAt, ShippedAt, DeliveredAt) VALUES
    (1, 1, 1, 28000, 3000, '2026-10-01 10:00', '2026-10-01 10:05', NULL, NULL),
    (2, 2, 2, 35000, 3000, '2026-10-01 11:00', '2026-10-01 11:02', '2026-10-02 09:00', NULL),
    (3, 2, 2, 38000, 0,    '2026-10-02 09:30', '2026-10-02 09:31', '2026-10-03 08:00', NULL),
    (4, 3, 2, 31000, 3000, '2026-10-02 14:00', '2026-10-02 14:10', '2026-10-03 10:00', NULL),
    (1, 1, 3, 15000, 3000, '2026-09-28 08:00', '2026-09-28 08:01', '2026-09-29 09:00', '2026-09-30 15:00'),
    (2, 3, 3, 28000, 0,    '2026-09-28 12:00', '2026-09-28 12:05', '2026-09-29 10:00', '2026-09-30 17:00'),
    (3, 1, 0, 10000, 3000, '2026-10-03 20:00', NULL, NULL, NULL),
    (4, 2, 9, 6000,  3000, '2026-09-25 09:00', '2026-09-25 09:02', NULL, NULL)
GO

INSERT INTO dbo.OrderItems (OrderId, ProductId, ProductName, Quantity, UnitPrice) VALUES
    (1, 1, N'머그컵', 2, 10000), (1, 2, N'컵받침', 1, 5000),
    (2, 3, N'프라이팬', 1, 32000),
    (3, 3, N'프라이팬', 1, 32000), (3, 4, N'행주 세트', 1, 6000),
    (4, 5, N'원목 트레이', 1, 28000),
    (5, 1, N'머그컵', 1, 10000), (5, 2, N'컵받침', 1, 5000),
    (6, 5, N'원목 트레이', 1, 28000),
    (7, 1, N'머그컵', 1, 10000),
    (8, 4, N'행주 세트', 1, 6000)
GO

INSERT INTO dbo.Shipments (OrderId, Carrier, TrackingNo, ShippedAt, DeliveredAt) VALUES
    (2, N'한빛택배', '100200300401', '2026-10-02 09:00', NULL),
    (3, N'한빛택배', '100200300402', '2026-10-03 08:00', NULL),
    (4, N'우리택배', '500600700801', '2026-10-03 10:00', NULL),
    (5, N'한빛택배', '100200300399', '2026-09-29 09:00', '2026-09-30 15:00'),
    (6, N'우리택배', '500600700799', '2026-09-29 10:00', '2026-09-30 17:00')
GO
