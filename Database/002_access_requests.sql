-- Idempotent platform contact-request table for existing single-business databases.
IF OBJECT_ID(N'dbo.AccessRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccessRequests (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccessRequests PRIMARY KEY,
        BusinessName nvarchar(120) NOT NULL,
        ContactName nvarchar(120) NOT NULL,
        Phone nvarchar(40) NOT NULL,
        Email nvarchar(160) NOT NULL,
        RequestedServices nvarchar(200) NOT NULL,
        Notes nvarchar(1000) NOT NULL,
        Status nvarchar(30) NOT NULL,
        CreatedAtUtc datetime2 NOT NULL
    );
END;
