IF OBJECT_ID(N'dbo.PlatformOperators', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlatformOperators (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_PlatformOperators PRIMARY KEY,
        Username nvarchar(100) NOT NULL,
        SecretHash varbinary(32) NOT NULL,
        IsActive bit NOT NULL,
        CreatedAtUtc datetime2 NOT NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PlatformOperators')
    AND name = N'IX_PlatformOperators_Username')
    CREATE UNIQUE INDEX IX_PlatformOperators_Username ON dbo.PlatformOperators (Username);
