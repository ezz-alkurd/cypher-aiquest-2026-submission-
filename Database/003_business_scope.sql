-- Idempotent upgrade of the existing single-business schema. Existing rows belong to the pilot.
IF COL_LENGTH(N'dbo.StoreProfiles', N'Slug') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD Slug nvarchar(80) NOT NULL CONSTRAINT DF_StoreProfiles_Slug DEFAULT N'';
IF COL_LENGTH(N'dbo.StoreProfiles', N'IsActive') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD IsActive bit NOT NULL CONSTRAINT DF_StoreProfiles_IsActive DEFAULT 1;
IF COL_LENGTH(N'dbo.StoreProfiles', N'InventoryEnabled') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD InventoryEnabled bit NOT NULL CONSTRAINT DF_StoreProfiles_InventoryEnabled DEFAULT 1;
IF COL_LENGTH(N'dbo.StoreProfiles', N'WhatsAppEnabled') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD WhatsAppEnabled bit NOT NULL CONSTRAINT DF_StoreProfiles_WhatsAppEnabled DEFAULT 0;
IF COL_LENGTH(N'dbo.StoreProfiles', N'StorefrontEnabled') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD StorefrontEnabled bit NOT NULL CONSTRAINT DF_StoreProfiles_StorefrontEnabled DEFAULT 1;
IF COL_LENGTH(N'dbo.StoreProfiles', N'WebsiteAssistantEnabled') IS NULL
    ALTER TABLE dbo.StoreProfiles ADD WebsiteAssistantEnabled bit NOT NULL CONSTRAINT DF_StoreProfiles_WebsiteAssistantEnabled DEFAULT 0;

EXEC(N'UPDATE dbo.StoreProfiles SET Slug = CONCAT(N''store-'', Id) WHERE Slug = N'''';');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoreProfiles_Slug' AND object_id = OBJECT_ID(N'dbo.StoreProfiles'))
    EXEC(N'CREATE UNIQUE INDEX IX_StoreProfiles_Slug ON dbo.StoreProfiles (Slug);');

IF COL_LENGTH(N'dbo.Categories', N'StoreId') IS NULL
    ALTER TABLE dbo.Categories ADD StoreId int NOT NULL CONSTRAINT DF_Categories_StoreId DEFAULT 1;
IF COL_LENGTH(N'dbo.SubCategories', N'StoreId') IS NULL
    ALTER TABLE dbo.SubCategories ADD StoreId int NOT NULL CONSTRAINT DF_SubCategories_StoreId DEFAULT 1;
IF COL_LENGTH(N'dbo.Products', N'StoreId') IS NULL
    ALTER TABLE dbo.Products ADD StoreId int NOT NULL CONSTRAINT DF_Products_StoreId DEFAULT 1;
IF COL_LENGTH(N'dbo.Orders', N'StoreId') IS NULL
    ALTER TABLE dbo.Orders ADD StoreId int NOT NULL CONSTRAINT DF_Orders_StoreId DEFAULT 1;
IF COL_LENGTH(N'dbo.OrderLines', N'StoreId') IS NULL
    ALTER TABLE dbo.OrderLines ADD StoreId int NOT NULL CONSTRAINT DF_OrderLines_StoreId DEFAULT 1;
IF COL_LENGTH(N'dbo.StockMovements', N'StoreId') IS NULL
    ALTER TABLE dbo.StockMovements ADD StoreId int NOT NULL CONSTRAINT DF_StockMovements_StoreId DEFAULT 1;

IF OBJECT_ID(N'dbo.OwnerAccounts', N'U') IS NULL
    CREATE TABLE dbo.OwnerAccounts (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_OwnerAccounts PRIMARY KEY,
        StoreId int NOT NULL,
        Username nvarchar(100) NOT NULL,
        PasswordHash nvarchar(500) NOT NULL,
        IsActive bit NOT NULL,
        CreatedAtUtc datetime2 NOT NULL
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OwnerAccounts_Username' AND object_id = OBJECT_ID(N'dbo.OwnerAccounts'))
    EXEC(N'CREATE UNIQUE INDEX IX_OwnerAccounts_Username ON dbo.OwnerAccounts (Username);');

-- Replace single-store uniqueness with business-specific uniqueness.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_Sku' AND object_id = OBJECT_ID(N'dbo.Products'))
    DROP INDEX IX_Products_Sku ON dbo.Products;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_StoreId_Sku' AND object_id = OBJECT_ID(N'dbo.Products'))
    EXEC(N'CREATE UNIQUE INDEX IX_Products_StoreId_Sku ON dbo.Products (StoreId, Sku);');
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Categories_Name' AND object_id = OBJECT_ID(N'dbo.Categories'))
    DROP INDEX IX_Categories_Name ON dbo.Categories;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Categories_StoreId_Name' AND object_id = OBJECT_ID(N'dbo.Categories'))
    EXEC(N'CREATE UNIQUE INDEX IX_Categories_StoreId_Name ON dbo.Categories (StoreId, Name);');
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubCategories_ParentCategoryId_Name' AND object_id = OBJECT_ID(N'dbo.SubCategories'))
    DROP INDEX IX_SubCategories_ParentCategoryId_Name ON dbo.SubCategories;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubCategories_StoreId_ParentCategoryId_Name' AND object_id = OBJECT_ID(N'dbo.SubCategories'))
    EXEC(N'CREATE UNIQUE INDEX IX_SubCategories_StoreId_ParentCategoryId_Name ON dbo.SubCategories (StoreId, ParentCategoryId, Name);');
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Orders_SourceChannel_ExternalOrderId' AND object_id = OBJECT_ID(N'dbo.Orders'))
    DROP INDEX IX_Orders_SourceChannel_ExternalOrderId ON dbo.Orders;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Orders_StoreId_SourceChannel_ExternalOrderId' AND object_id = OBJECT_ID(N'dbo.Orders'))
    EXEC(N'CREATE UNIQUE INDEX IX_Orders_StoreId_SourceChannel_ExternalOrderId ON dbo.Orders (StoreId, SourceChannel, ExternalOrderId) WHERE ExternalOrderId IS NOT NULL;');
