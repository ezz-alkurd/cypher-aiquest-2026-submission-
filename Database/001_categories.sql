-- Safe to run repeatedly. Upgrades databases created before categories were added.
-- Also usable directly in SSMS against an existing CypherCommerce database.
IF OBJECT_ID(N'dbo.Categories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Categories (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
        Name nvarchar(80) NOT NULL,
        Description nvarchar(300) NOT NULL,
        SortOrder int NOT NULL,
        IsActive bit NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.SubCategories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SubCategories (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubCategories PRIMARY KEY,
        ParentCategoryId int NOT NULL,
        Name nvarchar(80) NOT NULL,
        Description nvarchar(300) NOT NULL,
        SortOrder int NOT NULL,
        IsActive bit NOT NULL
    );
END;

IF COL_LENGTH(N'dbo.Products', N'SubCategoryId') IS NULL
    ALTER TABLE dbo.Products ADD SubCategoryId int NULL;

IF COL_LENGTH(N'dbo.Categories', N'StoreId') IS NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Categories_Name' AND object_id = OBJECT_ID(N'dbo.Categories'))
    CREATE UNIQUE INDEX IX_Categories_Name ON dbo.Categories (Name);

IF COL_LENGTH(N'dbo.SubCategories', N'StoreId') IS NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SubCategories_ParentCategoryId_Name' AND object_id = OBJECT_ID(N'dbo.SubCategories'))
    CREATE UNIQUE INDEX IX_SubCategories_ParentCategoryId_Name ON dbo.SubCategories (ParentCategoryId, Name);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_SubCategoryId' AND object_id = OBJECT_ID(N'dbo.Products'))
    CREATE INDEX IX_Products_SubCategoryId ON dbo.Products (SubCategoryId);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_SubCategories_Categories_ParentCategoryId')
    ALTER TABLE dbo.SubCategories ADD CONSTRAINT FK_SubCategories_Categories_ParentCategoryId
    FOREIGN KEY (ParentCategoryId) REFERENCES dbo.Categories (Id);

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Products_SubCategories_SubCategoryId')
    ALTER TABLE dbo.Products ADD CONSTRAINT FK_Products_SubCategories_SubCategoryId
    FOREIGN KEY (SubCategoryId) REFERENCES dbo.SubCategories (Id);
