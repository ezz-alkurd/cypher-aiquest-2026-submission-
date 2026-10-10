-- Additive, idempotent schema for owner service-change requests.
IF OBJECT_ID(N'dbo.StoreProfiles', N'U') IS NULL
    THROW 51200, 'Create the Cypher application schema before service requests.', 1;

IF OBJECT_ID(N'dbo.ServiceChangeRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ServiceChangeRequests (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ServiceChangeRequests PRIMARY KEY,
        StoreId int NOT NULL,
        ServiceCode nvarchar(32) NOT NULL,
        RequestedAction nvarchar(8) NOT NULL,
        Status nvarchar(12) NOT NULL CONSTRAINT DF_ServiceChangeRequests_Status DEFAULT N'Pending',
        OwnerNote nvarchar(500) NOT NULL CONSTRAINT DF_ServiceChangeRequests_OwnerNote DEFAULT N'',
        OperatorNote nvarchar(500) NOT NULL CONSTRAINT DF_ServiceChangeRequests_OperatorNote DEFAULT N'',
        CreatedAtUtc datetime2 NOT NULL CONSTRAINT DF_ServiceChangeRequests_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
        ResolvedAtUtc datetime2 NULL,
        ResolvedByOperatorId int NULL,
        CONSTRAINT FK_ServiceChangeRequests_StoreProfiles_StoreId FOREIGN KEY (StoreId) REFERENCES dbo.StoreProfiles(Id),
        CONSTRAINT CK_ServiceChangeRequests_ServiceCode CHECK (ServiceCode IN (N'inventory', N'whatsapp', N'storefront', N'website-assistant')),
        CONSTRAINT CK_ServiceChangeRequests_RequestedAction CHECK (RequestedAction IN (N'Add', N'Remove')),
        CONSTRAINT CK_ServiceChangeRequests_Status CHECK (Status IN (N'Pending', N'Approved', N'Rejected'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ServiceChangeRequests') AND name = N'IX_ServiceChangeRequests_StoreId_ServiceCode')
    CREATE UNIQUE INDEX IX_ServiceChangeRequests_StoreId_ServiceCode
        ON dbo.ServiceChangeRequests(StoreId, ServiceCode) WHERE Status = N'Pending';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ServiceChangeRequests') AND name = N'IX_ServiceChangeRequests_Status_CreatedAtUtc')
    CREATE INDEX IX_ServiceChangeRequests_Status_CreatedAtUtc
        ON dbo.ServiceChangeRequests(Status, CreatedAtUtc DESC);
