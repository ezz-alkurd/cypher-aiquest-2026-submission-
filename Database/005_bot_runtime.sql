-- Additive Cypher bot runtime. Run on an isolated test database first.
-- Does not seed stores, change passwords, reserve stock, or touch PlatformOperators.
SET XACT_ABORT ON;
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.StoreProfiles',N'U') IS NULL OR OBJECT_ID(N'dbo.Orders',N'U') IS NULL
    THROW 51100, 'The Cypher application schema is required first.', 1;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.BotStoreFacts',N'U') IS NULL
CREATE TABLE dbo.BotStoreFacts (
 StoreId int NOT NULL PRIMARY KEY REFERENCES dbo.StoreProfiles(Id),
 WorkingHours nvarchar(1000) NULL, DeliveryAreas nvarchar(1000) NULL,
 DeliveryPolicy nvarchar(1000) NULL, ReturnPolicy nvarchar(1000) NULL, OrderPolicy nvarchar(1000) NULL,
 DeliveryEnabled bit NULL, UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
);
IF OBJECT_ID(N'dbo.BotWhatsAppConnections',N'U') IS NULL
CREATE TABLE dbo.BotWhatsAppConnections (
 StoreId int NOT NULL PRIMARY KEY REFERENCES dbo.StoreProfiles(Id),
 OwnerAccountId int NOT NULL REFERENCES dbo.OwnerAccounts(Id),
 InstanceName nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
 ExpectedPhone nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL,
 Status nvarchar(20) NOT NULL DEFAULT N'pending',
 OnboardingToken nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL, OnboardingUntil datetime2 NULL,
 VerifiedAtUtc datetime2 NULL, UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_BotConnection_Status CHECK(Status IN(N'pending',N'ready',N'disconnected'))
);
IF OBJECT_ID(N'dbo.BotWebsiteVisitors',N'U') IS NULL
CREATE TABLE dbo.BotWebsiteVisitors (
 VisitorId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
 StoreId int NOT NULL REFERENCES dbo.StoreProfiles(Id),
 CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), ExpiresAtUtc datetime2 NOT NULL
);
IF OBJECT_ID(N'dbo.BotSessions',N'U') IS NULL
CREATE TABLE dbo.BotSessions (
 SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
 StoreId int NOT NULL REFERENCES dbo.StoreProfiles(Id),
 Channel nvarchar(12) NOT NULL, IdentityKey nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ConversationId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 LeaseToken nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL, LeaseUntil datetime2 NULL,
 ActiveMessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NULL,
 CartJson nvarchar(max) NOT NULL DEFAULT N'{"version":0,"status":"idle","items":[],"customerName":"","deliveryAddress":"","customerNote":"","deliveryFeeCents":0}',
 LastShownVersion int NULL, LastShownDeliveryStatus nvarchar(20) NULL, OrderId int NULL REFERENCES dbo.Orders(Id),
 CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 UNIQUE(StoreId,Channel,IdentityKey),
 CONSTRAINT CK_BotSession_Channel CHECK(Channel IN(N'whatsapp',N'website')),
 CONSTRAINT CK_BotSession_Cart CHECK(ISJSON(CartJson)=1)
);
IF OBJECT_ID(N'dbo.BotMessages',N'U') IS NULL
CREATE TABLE dbo.BotMessages (
 StoreId int NOT NULL, SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL REFERENCES dbo.BotSessions(SessionId),
 ConversationId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 MessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 InputJson nvarchar(max) NOT NULL, TextFingerprint varbinary(32) NOT NULL,
 Stage nvarchar(24) NOT NULL, SavedResponse nvarchar(max) NULL,
 CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(), UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(StoreId,SessionId,MessageId),
 CONSTRAINT CK_BotMessage_Input CHECK(ISJSON(InputJson)=1)
);
IF OBJECT_ID(N'dbo.BotCartOperations',N'U') IS NULL
CREATE TABLE dbo.BotCartOperations (
 StoreId int NOT NULL, SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 MessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 OperationId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 InputHash varbinary(32) NOT NULL, ResultJson nvarchar(max) NOT NULL,
 PRIMARY KEY(StoreId,SessionId,MessageId,OperationId),
 FOREIGN KEY(StoreId,SessionId,MessageId) REFERENCES dbo.BotMessages(StoreId,SessionId,MessageId)
);
IF OBJECT_ID(N'dbo.BotDeliveries',N'U') IS NULL
CREATE TABLE dbo.BotDeliveries (
 StoreId int NOT NULL, SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 MessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ClaimId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE,
 Reply nvarchar(4000) NOT NULL, CartVersion int NULL, Status nvarchar(20) NOT NULL,
 ProviderMessageId nvarchar(200) NULL, UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(StoreId,SessionId,MessageId),
 FOREIGN KEY(StoreId,SessionId,MessageId) REFERENCES dbo.BotMessages(StoreId,SessionId,MessageId),
 CONSTRAINT CK_BotDelivery_Status CHECK(Status IN(N'sending',N'sent',N'failed',N'unknown'))
);
IF OBJECT_ID(N'dbo.BotHistory',N'U') IS NULL
CREATE TABLE dbo.BotHistory (
 Id bigint IDENTITY PRIMARY KEY, StoreId int NOT NULL,
 SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ConversationId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 MessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 Role nvarchar(12) NOT NULL, Content nvarchar(max) NOT NULL,
 UNIQUE(StoreId,SessionId,MessageId,Role),
 FOREIGN KEY(StoreId,SessionId,MessageId) REFERENCES dbo.BotMessages(StoreId,SessionId,MessageId),
 CONSTRAINT CK_BotHistory_Role CHECK(Role IN(N'user',N'assistant')),
 CONSTRAINT CK_BotHistory_Content CHECK(DATALENGTH(Content)<=12000)
);
IF OBJECT_ID(N'dbo.BotRecoveryQueue',N'U') IS NULL
CREATE TABLE dbo.BotRecoveryQueue (
 StoreId int NOT NULL, SessionId nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 MessageId nvarchar(150) COLLATE Latin1_General_100_BIN2 NOT NULL,
 Reason nvarchar(40) NOT NULL, CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 ResolvedAtUtc datetime2 NULL,
 PRIMARY KEY(StoreId,SessionId,MessageId),
 FOREIGN KEY(StoreId,SessionId,MessageId) REFERENCES dbo.BotMessages(StoreId,SessionId,MessageId)
);
COMMIT;
GO

CREATE OR ALTER PROCEDURE dbo.CypherRegisterWebsiteVisitor
 @storeId int, @visitorId nvarchar(128)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 IF @visitorId IS NULL OR LEN(@visitorId)<16 OR LEN(@visitorId)>128 OR @visitorId COLLATE Latin1_General_100_BIN2 LIKE N'%[^-A-Za-z0-9_]%' 
   THROW 51101,'Invalid visitor identifier.',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM dbo.StoreProfiles WHERE Id=@storeId AND IsActive=1 AND StorefrontEnabled=1 AND WebsiteAssistantEnabled=1)
   THROW 51102,'Assistant unavailable.',1;
 IF EXISTS(SELECT 1 FROM dbo.BotWebsiteVisitors WITH(UPDLOCK,HOLDLOCK) WHERE VisitorId=@visitorId AND StoreId<>@storeId)
   THROW 51103,'Visitor scope conflict.',1;
 IF EXISTS(SELECT 1 FROM dbo.BotWebsiteVisitors WITH(UPDLOCK,HOLDLOCK) WHERE VisitorId=@visitorId)
   UPDATE dbo.BotWebsiteVisitors SET ExpiresAtUtc=DATEADD(day,7,SYSUTCDATETIME()) WHERE VisitorId=@visitorId AND StoreId=@storeId;
 ELSE INSERT dbo.BotWebsiteVisitors(VisitorId,StoreId,ExpiresAtUtc) VALUES(@visitorId,@storeId,DATEADD(day,7,SYSUTCDATETIME()));
 COMMIT;
END;
GO

-- Conservatively records orphaned turns. Unknown transport and partial mutations
-- must be reconciled by an operator, never automatically resubmitted to the Agent.
CREATE OR ALTER PROCEDURE dbo.CypherSweepBotRecovery
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 UPDATE d SET Status=N'unknown', UpdatedAtUtc=SYSUTCDATETIME()
 FROM dbo.BotDeliveries d JOIN dbo.BotSessions s ON s.SessionId=d.SessionId AND s.StoreId=d.StoreId
 WHERE d.Status=N'sending' AND s.LeaseUntil<SYSUTCDATETIME();
 INSERT dbo.BotRecoveryQueue(StoreId,SessionId,MessageId,Reason)
 SELECT m.StoreId,m.SessionId,m.MessageId,
 CASE WHEN d.Status IN(N'unknown',N'sending',N'sent') THEN N'delivery_reconciliation'
 WHEN EXISTS(SELECT 1 FROM dbo.BotCartOperations o WHERE o.StoreId=m.StoreId AND o.SessionId=m.SessionId AND o.MessageId=m.MessageId)
 THEN N'partial_mutation' ELSE N'interrupted_turn' END
 FROM dbo.BotMessages m JOIN dbo.BotSessions s ON s.SessionId=m.SessionId AND s.StoreId=m.StoreId
 LEFT JOIN dbo.BotDeliveries d ON d.StoreId=m.StoreId AND d.SessionId=m.SessionId AND d.MessageId=m.MessageId
 WHERE s.LeaseUntil<SYSUTCDATETIME() AND m.Stage NOT IN(N'completed',N'failed')
 AND NOT EXISTS(SELECT 1 FROM dbo.BotRecoveryQueue q WITH(UPDLOCK,HOLDLOCK) WHERE q.StoreId=m.StoreId AND q.SessionId=m.SessionId AND q.MessageId=m.MessageId);
 UPDATE m SET Stage=N'reconciliation',UpdatedAtUtc=SYSUTCDATETIME()
 FROM dbo.BotMessages m JOIN dbo.BotRecoveryQueue q ON q.StoreId=m.StoreId AND q.SessionId=m.SessionId AND q.MessageId=m.MessageId
 WHERE q.ResolvedAtUtc IS NULL AND m.Stage<>N'completed';
 COMMIT;
END;
GO
