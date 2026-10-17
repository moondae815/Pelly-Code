-- 샘플 코퍼스 스키마 (Shop)
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE dbo.Customers (
    CustomerId   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
    Name         nvarchar(50)  NOT NULL,
    Email        varchar(100)  NULL,
    Grade        tinyint       NOT NULL CONSTRAINT DF_Customers_Grade DEFAULT (1),
    CreatedAt    datetime2(0)  NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSDATETIME())
)
GO

CREATE TABLE dbo.Sellers (
    SellerId       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Sellers PRIMARY KEY,
    Name           nvarchar(100) NOT NULL,
    CommissionRate decimal(5,4)  NULL,
    IsActive       bit           NOT NULL CONSTRAINT DF_Sellers_IsActive DEFAULT (1)
)
GO

CREATE TABLE dbo.Products (
    ProductId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
    SellerId   int           NOT NULL CONSTRAINT FK_Products_Sellers REFERENCES dbo.Sellers (SellerId),
    Name       nvarchar(100) NOT NULL,
    Category   nvarchar(50)  NULL,
    Price      decimal(18,2) NOT NULL
)
GO

-- Status: 0 Pending, 1 Paid, 2 Shipped, 3 Delivered, 9 Cancelled
CREATE TABLE dbo.Orders (
    OrderId        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId     int           NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (CustomerId),
    SellerId       int           NOT NULL CONSTRAINT FK_Orders_Sellers REFERENCES dbo.Sellers (SellerId),
    Status         tinyint       NOT NULL,
    TotalAmount    decimal(18,2) NOT NULL,
    ShippingFee    decimal(18,2) NOT NULL CONSTRAINT DF_Orders_ShippingFee DEFAULT (0),
    DiscountAmount decimal(18,2) NOT NULL CONSTRAINT DF_Orders_DiscountAmount DEFAULT (0),
    PointUsed      decimal(18,2) NOT NULL CONSTRAINT DF_Orders_PointUsed DEFAULT (0),
    OrderedAt      datetime2(0)  NOT NULL,
    PaidAt         datetime2(0)  NULL,
    ShippedAt      datetime2(0)  NULL,
    DeliveredAt    datetime2(0)  NULL,
    CancelledAt    datetime2(0)  NULL,
    CancelReason   nvarchar(200) NULL
)
GO

CREATE INDEX IX_Orders_Customer_OrderedAt ON dbo.Orders (CustomerId, OrderedAt DESC)
GO
CREATE INDEX IX_Orders_Status_DeliveredAt ON dbo.Orders (Status, DeliveredAt)
GO

CREATE TABLE dbo.OrderItems (
    OrderItemId  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderItems PRIMARY KEY,
    OrderId      int           NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES dbo.Orders (OrderId),
    ProductId    int           NOT NULL CONSTRAINT FK_OrderItems_Products REFERENCES dbo.Products (ProductId),
    ProductName  nvarchar(100) NOT NULL,
    Quantity     int           NOT NULL,
    UnitPrice    decimal(18,2) NOT NULL,
    IsGiftItem   bit           NOT NULL CONSTRAINT DF_OrderItems_IsGiftItem DEFAULT (0),
    IsRefundable bit           NOT NULL CONSTRAINT DF_OrderItems_IsRefundable DEFAULT (1)
)
GO

CREATE INDEX IX_OrderItems_OrderId ON dbo.OrderItems (OrderId)
GO

CREATE TABLE dbo.Shipments (
    ShipmentId   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shipments PRIMARY KEY,
    OrderId      int          NOT NULL CONSTRAINT FK_Shipments_Orders REFERENCES dbo.Orders (OrderId),
    Carrier      nvarchar(30) NOT NULL,
    TrackingNo   varchar(30)  NOT NULL,
    ShippedAt    datetime2(0) NOT NULL,
    DeliveredAt  datetime2(0) NULL
)
GO

CREATE TABLE dbo.OrderHistories (
    HistoryId  bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_OrderHistories PRIMARY KEY,
    OrderId    int           NOT NULL,
    Message    nvarchar(400) NOT NULL,
    CreatedAt  datetime2(0)  NOT NULL
)
GO

CREATE TABLE dbo.Refunds (
    RefundId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Refunds PRIMARY KEY,
    OrderId      int           NOT NULL,
    Amount       decimal(18,2) NOT NULL,
    RequestedAt  datetime2(0)  NOT NULL
)
GO

CREATE TABLE dbo.Settlements (
    SettlementId     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Settlements PRIMARY KEY,
    BusinessDate     date          NOT NULL,
    SellerId         int           NOT NULL,
    Category         nvarchar(50)  NULL,
    OrderCount       int           NOT NULL,
    SalesAmount      decimal(18,2) NOT NULL,
    ShippingAmount   decimal(18,2) NOT NULL CONSTRAINT DF_Settlements_ShippingAmount DEFAULT (0),
    CommissionAmount decimal(18,2) NOT NULL,
    PayoutAmount     decimal(18,2) NOT NULL,
    CreatedAt        datetime2(0)  NOT NULL
)
GO

CREATE INDEX IX_Settlements_BusinessDate ON dbo.Settlements (BusinessDate, SellerId)
GO

CREATE TABLE dbo.SettlementDeferrals (
    DeferralId    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SettlementDeferrals PRIMARY KEY,
    BusinessDate  date          NOT NULL,
    SellerId      int           NOT NULL,
    Amount        decimal(18,2) NOT NULL
)
GO
