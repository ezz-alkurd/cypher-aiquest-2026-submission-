-- Parameterized boundary for the 16 Cypher SQL nodes. Install after 005_bot_runtime.sql.
-- Every call returns exactly one row containing payload JSON; no dynamic SQL.
CREATE OR ALTER PROCEDURE dbo.CypherBot
 @operation nvarchar(40), @input nvarchar(max), @output nvarchar(max)=NULL OUTPUT
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 SET ANSI_WARNINGS ON; SET ARITHABORT ON; SET CONCAT_NULL_YIELDS_NULL ON;
 SET NUMERIC_ROUNDABORT OFF;
 DECLARE @now datetime2=SYSUTCDATETIME(), @result nvarchar(max), @code nvarchar(80), @lockResult int,
 @storeId int=TRY_CONVERT(int,JSON_VALUE(@input,'$.storeId')),
 @sessionId nvarchar(64)=JSON_VALUE(@input,'$.sessionId'),
 @conversationId nvarchar(64)=JSON_VALUE(@input,'$.conversationId'),
 @messageId nvarchar(150)=JSON_VALUE(@input,'$.messageId'),
 @leaseToken nvarchar(64)=JSON_VALUE(@input,'$.leaseToken'),
 @channel nvarchar(12), @identity nvarchar(128), @instance nvarchar(100), @slug nvarchar(80),
 @stage nvarchar(24), @saved nvarchar(max), @cart nvarchar(max), @products nvarchar(max),
 @history nvarchar(max), @text nvarchar(max), @fingerprint varbinary(32), @leaseUntil datetime2,
 @candidate nvarchar(max), @version int, @fee int, @address nvarchar(500), @orderId int,
 @claimId nvarchar(64), @deliveryStatus nvarchar(20), @deliveryVersion int,
 @ownerId int, @ownerText nvarchar(200), @phone nvarchar(20), @onboarding nvarchar(64),
 @existing bit=0, @replay bit=0, @started bit=0, @outerTranCount int=@@TRANCOUNT;
 BEGIN TRY
  IF ISJSON(@input)<>1 OR DATALENGTH(@input)>400000 BEGIN SET @code=N'invalid_input'; GOTO Reject; END;
  SELECT @text=[text] FROM OPENJSON(@input) WITH([text] nvarchar(max) '$.text');
  IF LEN(JSON_VALUE(@input,'$.sessionId'))>64 OR LEN(JSON_VALUE(@input,'$.conversationId'))>64
   OR LEN(JSON_VALUE(@input,'$.messageId'))>150 OR LEN(JSON_VALUE(@input,'$.requestId'))>150
   OR LEN(JSON_VALUE(@input,'$.leaseToken'))>64 OR LEN(JSON_VALUE(@input,'$.visitorSessionId'))>128
   OR LEN(JSON_VALUE(@input,'$.recipient'))>15 OR LEN(JSON_VALUE(@input,'$.instanceName'))>100
   OR LEN(JSON_VALUE(@input,'$.expectedPhone'))>15
   BEGIN SET @code=N'invalid_input'; GOTO Reject; END;
  IF @operation=N'website.route'
  BEGIN
   SET @slug=JSON_VALUE(@input,'$.storefrontSlug'); SET @identity=JSON_VALUE(@input,'$.visitorSessionId');
   SET @messageId=JSON_VALUE(@input,'$.requestId'); SET @channel=N'website';
   SELECT @storeId=Id FROM dbo.StoreProfiles WHERE Slug=@slug AND IsActive=1 AND StorefrontEnabled=1;
   IF @storeId IS NULL BEGIN SET @code=N'store_not_found'; GOTO Reject; END;
  END;
  IF @operation=N'route'
  BEGIN
   SET @instance=JSON_VALUE(@input,'$.instanceName'); SET @identity=JSON_VALUE(@input,'$.recipient'); SET @channel=N'whatsapp';
   SELECT @storeId=c.StoreId FROM dbo.BotWhatsAppConnections c JOIN dbo.StoreProfiles s ON s.Id=c.StoreId
   WHERE c.InstanceName=@instance AND c.Status=N'ready' AND s.IsActive=1 AND s.WhatsAppEnabled=1;
   IF @storeId IS NULL BEGIN SET @code=N'whatsapp_not_ready'; GOTO Reject; END;
   IF LEN(@identity)<8 OR LEN(@identity)>15 OR LEFT(@identity,1)=N'0' OR @identity COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9]%'
    BEGIN SET @code=N'invalid_recipient'; GOTO Reject; END;
  END;
  IF @storeId IS NULL OR @storeId<=0 BEGIN SET @code=N'store_scope_missing'; GOTO Reject; END;
  BEGIN TRANSACTION; SET @started=1;
  IF @outerTranCount>0 SAVE TRANSACTION CypherBotCall;
  -- A short per-business transaction serializes claims/CAS; no transaction spans an AI call.
  DECLARE @resource nvarchar(100)=CONCAT(N'CypherBot:',@storeId);
  EXEC @lockResult=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
  IF @lockResult<0 BEGIN SET @code=N'conversation_busy'; GOTO Reject; END;
  IF NOT EXISTS(SELECT 1 FROM dbo.StoreProfiles WHERE Id=@storeId AND IsActive=1) BEGIN SET @code=N'store_not_found'; GOTO Reject; END;

  IF @operation IN(N'onboarding.authorize',N'onboarding.pending',N'onboarding.ready')
  BEGIN
   SET @ownerText=JSON_VALUE(@input,'$.ownerUserId'); SET @ownerId=TRY_CONVERT(int,@ownerText);
   SET @phone=JSON_VALUE(@input,'$.expectedPhone'); SET @instance=CONCAT(N'cypher-',@storeId);
   IF NOT EXISTS(SELECT 1 FROM dbo.OwnerAccounts WHERE Id=@ownerId AND StoreId=@storeId AND IsActive=1)
    OR NOT EXISTS(SELECT 1 FROM dbo.StoreProfiles WHERE Id=@storeId AND WhatsAppEnabled=1)
    BEGIN SET @code=N'owner_store_not_authorized'; GOTO Reject; END;
   IF @phone IS NULL OR LEN(@phone)<8 OR LEN(@phone)>15 OR LEFT(@phone,1)=N'0' OR @phone COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9]%'
    BEGIN SET @code=N'expected_phone_missing'; GOTO Reject; END;
   IF @operation=N'onboarding.authorize'
   BEGIN
    IF EXISTS(SELECT 1 FROM dbo.BotWhatsAppConnections WHERE StoreId=@storeId AND OnboardingUntil>@now)
     BEGIN SET @code=N'conversation_busy'; GOTO Reject; END;
    SET @onboarding=CONVERT(nvarchar(64),NEWID());
    IF NOT EXISTS(SELECT 1 FROM dbo.BotWhatsAppConnections WHERE StoreId=@storeId)
     INSERT dbo.BotWhatsAppConnections(StoreId,OwnerAccountId,InstanceName,ExpectedPhone,OnboardingToken,OnboardingUntil)
     VALUES(@storeId,@ownerId,@instance,@phone,@onboarding,DATEADD(minute,5,@now));
    ELSE UPDATE dbo.BotWhatsAppConnections SET OwnerAccountId=@ownerId,ExpectedPhone=@phone,
      Status=CASE WHEN ExpectedPhone<>@phone OR JSON_VALUE(@input,'$.action')=N'connect' THEN N'pending' ELSE Status END,
      OnboardingToken=@onboarding,OnboardingUntil=DATEADD(minute,5,@now),UpdatedAtUtc=@now WHERE StoreId=@storeId;
    SET @result=(SELECT CAST(1 AS bit) authorized,@storeId storeId,@ownerText ownerUserId,@phone expectedPhone,
      @instance instanceName,@onboarding onboardingToken FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
   END
   ELSE
   BEGIN
    SET @onboarding=JSON_VALUE(@input,'$.onboardingToken');
    IF NOT EXISTS(SELECT 1 FROM dbo.BotWhatsAppConnections WHERE StoreId=@storeId AND OwnerAccountId=@ownerId
      AND ExpectedPhone=@phone AND InstanceName=JSON_VALUE(@input,'$.instanceName')
      AND OnboardingToken=@onboarding AND OnboardingUntil>@now) BEGIN SET @code=N'onboarding_scope_mismatch'; GOTO Reject; END;
    IF @operation=N'onboarding.ready' AND (ISNULL(JSON_VALUE(@input,'$.ownerVerified'),N'')<>N'true'
      OR ISNULL(JSON_VALUE(@input,'$.webhookVerified'),N'')<>N'true') BEGIN SET @code=N'connection_not_verified'; GOTO Reject; END;
    UPDATE dbo.BotWhatsAppConnections SET Status=CASE WHEN @operation=N'onboarding.ready' THEN N'ready' ELSE N'pending' END,
      VerifiedAtUtc=CASE WHEN @operation=N'onboarding.ready' THEN @now ELSE NULL END,
      OnboardingUntil=NULL,OnboardingToken=NULL,UpdatedAtUtc=@now WHERE StoreId=@storeId;
    SET @result=(SELECT CAST(1 AS bit) success,@storeId storeId,@ownerText ownerUserId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
   END;
   GOTO Emit;
  END;

  IF @operation IN(N'route',N'website.route')
  BEGIN
   IF @messageId IS NULL OR LEN(@messageId)=0 OR @identity IS NULL OR LEN(@identity)=0 BEGIN SET @code=N'invalid_input'; GOTO Reject; END;
   IF @channel=N'website'
   BEGIN
    IF NOT EXISTS(SELECT 1 FROM dbo.StoreProfiles WHERE Id=@storeId AND WebsiteAssistantEnabled=1) BEGIN SET @code=N'assistant_disabled'; GOTO Reject; END;
    IF NOT EXISTS(SELECT 1 FROM dbo.BotWebsiteVisitors WHERE VisitorId=@identity AND StoreId=@storeId AND ExpiresAtUtc>@now)
     BEGIN SET @code=N'session_conflict'; GOTO Reject; END;
    IF (SELECT COUNT(*) FROM dbo.BotMessages m JOIN dbo.BotSessions s ON s.SessionId=m.SessionId AND s.StoreId=m.StoreId
       WHERE m.StoreId=@storeId AND s.Channel=N'website' AND m.CreatedAtUtc>DATEADD(minute,-1,@now))>=120
     BEGIN SET @code=N'rate_limit_exceeded'; GOTO Reject; END;
   END;
   SELECT @sessionId=SessionId,@conversationId=ConversationId,@leaseUntil=LeaseUntil,@cart=CartJson
    FROM dbo.BotSessions WHERE StoreId=@storeId AND Channel=@channel AND IdentityKey=@identity;
   IF @sessionId IS NULL
   BEGIN
    SET @sessionId=CONVERT(nvarchar(64),NEWID()); SET @conversationId=CONVERT(nvarchar(64),NEWID());
    INSERT dbo.BotSessions(SessionId,StoreId,Channel,IdentityKey,ConversationId)
     VALUES(@sessionId,@storeId,@channel,@identity,@conversationId);
   END;
   IF @channel=N'whatsapp' AND EXISTS(SELECT 1 FROM dbo.BotMessages m JOIN dbo.BotSessions s ON s.SessionId=m.SessionId
      WHERE m.StoreId=@storeId AND m.MessageId=@messageId AND s.Channel=N'whatsapp' AND s.IdentityKey<>@identity)
    BEGIN SET @code=N'message_scope_mismatch'; GOTO Reject; END;
   IF NULLIF(@text,N'') IS NULL OR LEN(@text)>CASE WHEN @channel=N'website' THEN 2000 ELSE 6000 END
    BEGIN SET @code=N'invalid_input'; GOTO Reject; END;
   SET @fingerprint=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@text));
   SELECT @stage=Stage,@saved=SavedResponse,@existing=1 FROM dbo.BotMessages
    WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   IF @existing=1 AND EXISTS(SELECT 1 FROM dbo.BotMessages WHERE StoreId=@storeId AND SessionId=@sessionId
     AND MessageId=@messageId AND TextFingerprint<>@fingerprint) BEGIN SET @code=N'request_conflict'; GOTO Reject; END;
   IF @stage=N'completed'
   BEGIN
    IF @channel=N'whatsapp' SET @result=N'{"ignored":true,"reason":"duplicate_completed"}';
    ELSE SELECT @result=(SELECT CAST(1 AS bit) ok,@storeId storeId,@slug storefrontSlug,@identity visitorSessionId,
      @sessionId sessionId,m.ConversationId conversationId,@messageId messageId,N'website' channel,
      CAST(1 AS bit) storeActive,CAST(1 AS bit) assistantEnabled,CAST(1 AS bit) replay,CAST(0 AS bit) acquired,
      JSON_QUERY(m.SavedResponse) savedResponse FROM dbo.BotMessages m WHERE StoreId=@storeId AND SessionId=@sessionId
      AND MessageId=@messageId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    GOTO Emit;
   END;
   IF @leaseUntil>@now BEGIN SET @code=N'conversation_busy'; GOTO Reject; END;
   IF @channel=N'website' AND @existing=0 AND (SELECT COUNT(*) FROM dbo.BotMessages
      WHERE StoreId=@storeId AND SessionId=@sessionId AND CreatedAtUtc>DATEADD(minute,-1,@now))>=10
    BEGIN SET @code=N'rate_limit_exceeded'; GOTO Reject; END;
   IF @channel=N'whatsapp' AND @existing=1
   BEGIN
    IF EXISTS(SELECT 1 FROM dbo.BotDeliveries WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId
        AND Status IN(N'sending',N'unknown',N'sent'))
      OR (EXISTS(SELECT 1 FROM dbo.BotCartOperations WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId)
          AND NOT EXISTS(SELECT 1 FROM dbo.BotDeliveries WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND Status=N'failed'))
    BEGIN
     UPDATE dbo.BotMessages SET Stage=N'reconciliation',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
     IF NOT EXISTS(SELECT 1 FROM dbo.BotRecoveryQueue WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId)
      INSERT dbo.BotRecoveryQueue(StoreId,SessionId,MessageId,Reason) VALUES(@storeId,@sessionId,@messageId,N'interrupted_whatsapp');
     SET @result=N'{"ok":false,"code":"reconciliation_required"}'; GOTO Emit;
    END;
    SELECT @saved=Reply FROM dbo.BotDeliveries WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND Status=N'failed';
    IF @saved IS NOT NULL SET @replay=1;
   END;
   IF @channel=N'whatsapp' AND @existing=0 AND JSON_VALUE(@cart,'$.status')=N'submitted'
      AND REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@text)),N'أ',N'ا'),N'إ',N'ا'),N'آ',N'ا') COLLATE Latin1_General_100_BIN2<>N'تاكيد'
   BEGIN
    SET @conversationId=CONVERT(nvarchar(64),NEWID());
    UPDATE dbo.BotSessions SET ConversationId=@conversationId,OrderId=NULL,LastShownVersion=NULL,LastShownDeliveryStatus=NULL,
     CartJson=N'{"version":0,"status":"idle","items":[],"customerName":"","deliveryAddress":"","customerNote":"","deliveryFeeCents":0}' WHERE SessionId=@sessionId;
   END;
   IF @existing=1 SELECT @conversationId=ConversationId FROM dbo.BotMessages WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   SET @leaseToken=CONVERT(nvarchar(64),NEWID());
   UPDATE dbo.BotSessions SET LeaseToken=@leaseToken,LeaseUntil=DATEADD(minute,5,@now),ActiveMessageId=@messageId WHERE SessionId=@sessionId;
   IF @existing=0 INSERT dbo.BotMessages(StoreId,SessionId,ConversationId,MessageId,InputJson,TextFingerprint,Stage)
     VALUES(@storeId,@sessionId,@conversationId,@messageId,@input,@fingerprint,N'routed');
   ELSE UPDATE dbo.BotMessages SET Stage=N'routed',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   SELECT @history=(SELECT Role role,LEFT(Content,1500) content FROM
    (SELECT TOP(12) Id,Role,Content FROM dbo.BotHistory WHERE StoreId=@storeId AND SessionId=@sessionId AND ConversationId=@conversationId ORDER BY Id DESC) h
    ORDER BY Id FOR JSON PATH);
   SET @result=(SELECT CAST(1 AS bit) ok,CAST(0 AS bit) ignored,@storeId storeId,@sessionId sessionId,@conversationId conversationId,
    @messageId messageId,@leaseToken leaseToken,@channel channel,CAST(1 AS bit) storeActive,
    CAST(1 AS bit) acquired,@replay replay FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
   IF @channel=N'website'
   BEGIN
    SET @result=JSON_MODIFY(@result,'$.storefrontSlug',@slug); SET @result=JSON_MODIFY(@result,'$.visitorSessionId',@identity);
    SET @result=JSON_MODIFY(@result,'$.assistantEnabled',CAST(1 AS bit)); SET @result=JSON_MODIFY(@result,'$.history',JSON_QUERY(COALESCE(@history,N'[]')));
   END
   ELSE
   BEGIN
    SET @result=JSON_MODIFY(@result,'$.instanceName',@instance); SET @result=JSON_MODIFY(@result,'$.recipient',@identity);
    SET @result=JSON_MODIFY(@result,'$.whatsappReady',CAST(1 AS bit)); SET @result=JSON_MODIFY(@result,'$.historyText',@history);
    IF @replay=1
    BEGIN
     SET @result=JSON_MODIFY(@result,'$.savedReply',@saved);
     SELECT @deliveryVersion=CartVersion FROM dbo.BotDeliveries WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
     SET @result=JSON_MODIFY(@result,'$.savedCartVersion',ISNULL(@deliveryVersion,0));
    END;
   END;
   GOTO Emit;
  END;

  -- Every remaining operation checks the exact active turn, not just StoreId.
  SELECT @channel=Channel,@identity=IdentityKey,@cart=CartJson,@orderId=OrderId
   FROM dbo.BotSessions WHERE StoreId=@storeId AND SessionId=@sessionId AND ConversationId=@conversationId
    AND ActiveMessageId=@messageId AND LeaseToken=@leaseToken AND LeaseUntil>@now;
  IF @channel IS NULL BEGIN SET @code=N'lease_scope_mismatch'; GOTO Reject; END;
  IF NOT EXISTS(SELECT 1 FROM dbo.BotMessages WHERE StoreId=@storeId AND SessionId=@sessionId AND ConversationId=@conversationId
     AND MessageId=@messageId AND Stage IN(N'routed',N'cart_mutated',N'prepared',N'sent',N'failed',N'unknown'))
   BEGIN SET @code=N'message_scope_mismatch'; GOTO Reject; END;
  IF @channel=N'website' AND NOT EXISTS(SELECT 1 FROM dbo.StoreProfiles WHERE Id=@storeId AND StorefrontEnabled=1 AND WebsiteAssistantEnabled=1)
   BEGIN SET @code=N'assistant_disabled'; GOTO Reject; END;
  IF @channel=N'whatsapp' AND NOT EXISTS(SELECT 1 FROM dbo.BotWhatsAppConnections c JOIN dbo.StoreProfiles s ON s.Id=c.StoreId
    WHERE c.StoreId=@storeId AND c.Status=N'ready' AND s.WhatsAppEnabled=1) BEGIN SET @code=N'whatsapp_not_ready'; GOTO Reject; END;
  IF @channel=N'website' AND @operation NOT IN(N'store',N'website.catalog',N'website.finish') BEGIN SET @code=N'wrong_channel'; GOTO Reject; END;
  SET @result=(SELECT CAST(1 AS bit) success,@storeId storeId,@sessionId sessionId,@conversationId conversationId,
   @messageId messageId,@leaseToken leaseToken,@channel channel FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);

  IF @operation=N'store'
  BEGIN
   DECLARE @facts nvarchar(max)=(SELECT s.Name name,NULLIF(s.Phone,N'') phone,NULLIF(s.Address,N'') location,
    f.WorkingHours workingHours,f.DeliveryAreas deliveryAreas,
    CASE WHEN f.DeliveryEnabled=0 THEN CONCAT(N'لا يوجد توصيل. ',f.DeliveryPolicy)
     WHEN f.DeliveryEnabled=1 THEN CONCAT(N'التوصيل متاح. ',f.DeliveryPolicy) ELSE f.DeliveryPolicy END deliveryPolicy,
    f.ReturnPolicy returnPolicy,f.OrderPolicy orderPolicy
    FROM dbo.StoreProfiles s LEFT JOIN dbo.BotStoreFacts f ON f.StoreId=s.Id WHERE s.Id=@storeId
    FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER);
   SET @result=JSON_MODIFY(@result,'$.store',JSON_QUERY(@facts)); GOTO Emit;
  END;

  IF @operation IN(N'inventory',N'website.catalog',N'cart.read',N'confirm.read')
  BEGIN
   IF @operation=N'website.catalog' AND @channel<>N'website' BEGIN SET @code=N'wrong_channel'; GOTO Reject; END;
   IF @operation=N'website.catalog' AND (SELECT COUNT(*) FROM dbo.Products WHERE StoreId=@storeId AND IsActive=1)>5000
    BEGIN SET @code=N'catalog_limit_exceeded'; GOTO Reject; END;
   SELECT @products=(SELECT p.StoreId storeId,p.Id productId,p.Name name,p.Sku sku,p.Aliases aliases,p.Description description,
    p.Category category,p.Unit unit,CONVERT(int,p.Price*100) priceCents,p.StockQuantity stockQuantity,
    CAST(CASE WHEN p.IsActive=1 AND sub.IsActive=1 AND cat.IsActive=1 AND sub.StoreId=@storeId AND cat.StoreId=@storeId THEN 1 ELSE 0 END AS bit) isActive,
    p.ImageUrl imageUrl,CONCAT(N'/s/',s.Slug,N'/products/',p.Id) productUrl
    FROM dbo.Products p JOIN dbo.StoreProfiles s ON s.Id=p.StoreId
    LEFT JOIN dbo.SubCategories sub ON sub.Id=p.SubCategoryId LEFT JOIN dbo.Categories cat ON cat.Id=sub.ParentCategoryId
    WHERE p.StoreId=@storeId AND (@operation IN(N'cart.read',N'confirm.read') OR
      (p.IsActive=1 AND sub.IsActive=1 AND cat.IsActive=1 AND sub.StoreId=@storeId AND cat.StoreId=@storeId))
    ORDER BY p.Id FOR JSON PATH,INCLUDE_NULL_VALUES);
   SET @result=JSON_MODIFY(@result,'$.products',JSON_QUERY(@products));
   IF @operation=N'website.catalog' SET @result=JSON_MODIFY(@result,'$.catalogComplete',CAST(1 AS bit));
   IF @operation IN(N'cart.read',N'confirm.read')
   BEGIN
    SET @address=CASE WHEN JSON_VALUE(@input,'$.action')=N'update_customer' AND NULLIF(JSON_VALUE(@input,'$.deliveryAddress'),N'') IS NOT NULL
     THEN JSON_VALUE(@input,'$.deliveryAddress') ELSE JSON_VALUE(@cart,'$.deliveryAddress') END;
    SELECT @fee=CONVERT(int,DeliveryFee*100) FROM dbo.StoreProfiles WHERE Id=@storeId;
    SET @result=JSON_MODIFY(@result,'$.cart',JSON_QUERY(@cart)); SET @result=JSON_MODIFY(@result,'$.deliveryFeeCents',@fee);
    IF @operation=N'confirm.read'
    BEGIN
     DECLARE @shownVersion int,@shownStatus nvarchar(20);
     SELECT @shownVersion=LastShownVersion,@shownStatus=LastShownDeliveryStatus FROM dbo.BotSessions WHERE SessionId=@sessionId;
     SET @result=JSON_MODIFY(@result,'$.lastShownVersion',@shownVersion); SET @result=JSON_MODIFY(@result,'$.lastShownDeliveryStatus',@shownStatus);
     IF @orderId IS NOT NULL
     BEGIN
      DECLARE @priorOrder nvarchar(max)=(SELECT Id id,OrderNumber orderNumber,N'NeedsReview' status,CONVERT(int,Total*100) totalCents
       FROM dbo.Orders WHERE Id=@orderId AND StoreId=@storeId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      SET @result=JSON_MODIFY(@result,'$.existingOrder',JSON_QUERY(@priorOrder));
     END;
    END;
   END;
   GOTO Emit;
  END;

  IF @operation IN(N'cart.commit',N'confirm.commit')
  BEGIN
   SET @candidate=CASE WHEN @operation=N'cart.commit' THEN JSON_QUERY(@input,'$.candidate') ELSE JSON_QUERY(@input,'$.cart') END;
   SET @version=TRY_CONVERT(int,JSON_VALUE(@input,'$.expectedVersion'));
   DECLARE @opId nvarchar(150)=JSON_VALUE(@input,'$.operationId'),@inputHash varbinary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@input)),
    @opSaved nvarchar(max),@status nvarchar(20),@total bigint,@subtotal bigint,@newFee int;
   IF @operation=N'cart.commit'
   BEGIN
    IF NULLIF(@opId,N'') IS NULL BEGIN SET @code=N'operation_id_missing'; GOTO Reject; END;
    SELECT @opSaved=ResultJson FROM dbo.BotCartOperations WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND OperationId=@opId;
    IF @opSaved IS NOT NULL
    BEGIN
     IF EXISTS(SELECT 1 FROM dbo.BotCartOperations WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND OperationId=@opId AND InputHash<>@inputHash)
      BEGIN SET @code=N'operation_conflict'; GOTO Reject; END;
     SET @result=@opSaved; SET @result=JSON_MODIFY(@result,'$.leaseToken',@leaseToken); GOTO Emit;
    END;
    IF JSON_VALUE(@cart,'$.status')=N'submitted' BEGIN SET @code=N'submitted_cart_requires_new_conversation'; GOTO Reject; END;
   END
   ELSE
   BEGIN
    DECLARE @confirmation nvarchar(100)=LTRIM(RTRIM(JSON_VALUE(@input,'$.confirmationText')));
    IF REPLACE(REPLACE(REPLACE(ISNULL(@confirmation,N''),N'أ',N'ا'),N'إ',N'ا'),N'آ',N'ا') COLLATE Latin1_General_100_BIN2<>N'تاكيد' BEGIN SET @code=N'explicit_confirmation_required'; GOTO Reject; END;
    IF @orderId IS NOT NULL
    BEGIN
     DECLARE @existingOrder nvarchar(max)=(SELECT Id id,OrderNumber orderNumber,N'NeedsReview' status,CONVERT(int,Total*100) totalCents
      FROM dbo.Orders WHERE Id=@orderId AND StoreId=@storeId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
     SET @result=JSON_MODIFY(@result,'$.order',JSON_QUERY(@existingOrder)); GOTO Emit;
    END;
    IF NOT EXISTS(SELECT 1 FROM dbo.BotSessions WHERE SessionId=@sessionId AND LastShownVersion=@version AND LastShownDeliveryStatus=N'sent')
     BEGIN SET @code=N'latest_summary_not_delivered'; GOTO Reject; END;
    -- Persisted cart is authoritative, not the caller's checkout JSON.
    SET @candidate=@cart;
   END;
   IF @version IS NULL OR TRY_CONVERT(int,JSON_VALUE(@cart,'$.version'))<>@version BEGIN SET @code=N'cart_version_conflict'; GOTO Reject; END;
   IF @candidate IS NULL OR ISJSON(@candidate)<>1 OR JSON_QUERY(@candidate,'$.items') IS NULL BEGIN SET @code=N'invalid_cart'; GOTO Reject; END;
   IF @operation=N'cart.commit' AND ISNULL(TRY_CONVERT(int,JSON_VALUE(@candidate,'$.version')),-1)<>@version+1 BEGIN SET @code=N'cart_version_conflict'; GOTO Reject; END;
   DECLARE @lines TABLE(ProductId int,Quantity int,PriceCents int,Name nvarchar(200));
   INSERT @lines SELECT productId,quantity,priceCents,name FROM OPENJSON(@candidate,'$.items')
    WITH(productId int,quantity int,priceCents int,name nvarchar(200));
   IF (SELECT COUNT(*) FROM @lines)>100 OR EXISTS(SELECT ProductId FROM @lines GROUP BY ProductId HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM @lines WHERE ProductId IS NULL OR Quantity IS NULL OR Quantity<1 OR Quantity>100000 OR PriceCents IS NULL OR PriceCents<0)
    BEGIN SET @code=N'invalid_cart'; GOTO Reject; END;
   IF EXISTS(SELECT 1 FROM @lines l LEFT JOIN dbo.Products p WITH(UPDLOCK,HOLDLOCK) ON p.Id=l.ProductId AND p.StoreId=@storeId
     LEFT JOIN dbo.SubCategories sub ON sub.Id=p.SubCategoryId AND sub.StoreId=@storeId
     LEFT JOIN dbo.Categories cat ON cat.Id=sub.ParentCategoryId AND cat.StoreId=@storeId
     WHERE p.Id IS NULL OR p.IsActive<>1 OR ISNULL(sub.IsActive,0)<>1 OR ISNULL(cat.IsActive,0)<>1)
    BEGIN SET @code=N'product_not_available'; GOTO Reject; END;
   IF EXISTS(SELECT 1 FROM @lines l JOIN dbo.Products p WITH(UPDLOCK,HOLDLOCK) ON p.Id=l.ProductId AND p.StoreId=@storeId WHERE p.StockQuantity<l.Quantity)
    BEGIN SET @code=N'insufficient_stock'; GOTO Reject; END;
   IF EXISTS(SELECT 1 FROM @lines l JOIN dbo.Products p WITH(UPDLOCK,HOLDLOCK) ON p.Id=l.ProductId AND p.StoreId=@storeId
     WHERE CONVERT(int,p.Price*100)<>l.PriceCents) BEGIN SET @code=N'price_changed_requires_new_summary'; GOTO Reject; END;
   SET @address=JSON_VALUE(@candidate,'$.deliveryAddress');
   SELECT @newFee=CONVERT(int,DeliveryFee*100) FROM dbo.StoreProfiles WITH(UPDLOCK,HOLDLOCK) WHERE Id=@storeId;
   IF EXISTS(SELECT 1 FROM @lines)
   BEGIN
    IF NOT EXISTS(SELECT 1 FROM dbo.BotStoreFacts WHERE StoreId=@storeId AND DeliveryEnabled=1 AND NULLIF(DeliveryAreas,N'') IS NOT NULL)
     BEGIN SET @code=N'delivery_unavailable'; GOTO Reject; END;
    -- Areas are explicit comma-separated owner values; '*' explicitly permits all addresses.
    IF NULLIF(@address,N'') IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.BotStoreFacts f
       CROSS APPLY STRING_SPLIT(f.DeliveryAreas,N',') a WHERE f.StoreId=@storeId AND NULLIF(LTRIM(RTRIM(a.value)),N'') IS NOT NULL
       AND (LTRIM(RTRIM(a.value))=N'*' OR CHARINDEX(LTRIM(RTRIM(a.value)),@address)>0))
     BEGIN SET @code=N'delivery_unavailable'; GOTO Reject; END;
    IF ISNULL(TRY_CONVERT(int,JSON_VALUE(@candidate,'$.deliveryFeeCents')),-1)<>@newFee BEGIN SET @code=N'delivery_changed_requires_new_summary'; GOTO Reject; END;
   END
   ELSE SET @newFee=0;
   SELECT @subtotal=ISNULL(SUM(CONVERT(bigint,Quantity)*PriceCents),0) FROM @lines;
   SET @total=@subtotal+@newFee;
   IF @total>1000000000 BEGIN SET @code=N'order_total_limit'; GOTO Reject; END;
   SET @candidate=JSON_MODIFY(@candidate,'$.items',JSON_QUERY((SELECT l.ProductId productId,p.Name name,l.Quantity quantity,l.PriceCents priceCents
     FROM @lines l JOIN dbo.Products p ON p.Id=l.ProductId AND p.StoreId=@storeId ORDER BY l.ProductId FOR JSON PATH)));
   SET @candidate=JSON_MODIFY(@candidate,'$.subtotalCents',@subtotal); SET @candidate=JSON_MODIFY(@candidate,'$.totalCents',@total);
   SET @candidate=JSON_MODIFY(@candidate,'$.deliveryFeeCents',@newFee);
   IF @operation=N'cart.commit'
   BEGIN
    SET @status=CASE WHEN EXISTS(SELECT 1 FROM @lines) THEN N'building_order' ELSE N'idle' END;
    SET @candidate=JSON_MODIFY(@candidate,'$.status',@status);
    UPDATE dbo.BotSessions SET CartJson=@candidate,LastShownVersion=NULL,LastShownDeliveryStatus=NULL WHERE SessionId=@sessionId;
    SET @result=JSON_MODIFY(@result,'$.cart',JSON_QUERY(@candidate));
    INSERT dbo.BotCartOperations(StoreId,SessionId,MessageId,OperationId,InputHash,ResultJson)
     VALUES(@storeId,@sessionId,@messageId,@opId,@inputHash,@result);
    UPDATE dbo.BotMessages SET Stage=N'cart_mutated',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   END
   ELSE
   BEGIN
    IF NOT EXISTS(SELECT 1 FROM @lines) OR NULLIF(JSON_VALUE(@candidate,'$.customerName'),N'') IS NULL OR NULLIF(@address,N'') IS NULL
     BEGIN SET @code=N'checkout_fields_missing'; GOTO Reject; END;
    DECLARE @external nvarchar(150)=CONCAT(N'wa:',@conversationId,N':v',@version),
      @number nvarchar(24)=CONCAT(N'CY-',FORMAT(@now,'yyMMdd'),N'-',UPPER(LEFT(REPLACE(CONVERT(nvarchar(36),NEWID()),N'-',N''),8)));
    IF ISNULL(JSON_VALUE(@input,'$.externalOrderId'),N'')<>@external BEGIN SET @code=N'checkout_scope_mismatch'; GOTO Reject; END;
    SELECT @orderId=Id FROM dbo.Orders WITH(UPDLOCK,HOLDLOCK) WHERE StoreId=@storeId AND SourceChannel=N'whatsapp' AND ExternalOrderId=@external;
    IF @orderId IS NULL
    BEGIN
     INSERT dbo.Orders(StoreId,OrderNumber,SourceChannel,CustomerName,Phone,DeliveryAddress,CustomerNote,OwnerNote,
      ExternalOrderId,ExternalConversationId,Status,Subtotal,DeliveryFee,Total,StockReserved,CreatedAtUtc)
     VALUES(@storeId,@number,N'whatsapp',LEFT(JSON_VALUE(@candidate,'$.customerName'),150),@identity,@address,
      LEFT(ISNULL(JSON_VALUE(@candidate,'$.customerNote'),N''),1000),N'',@external,@conversationId,0,
      CONVERT(decimal(18,2),@subtotal)/100,CONVERT(decimal(18,2),@newFee)/100,CONVERT(decimal(18,2),@total)/100,0,@now);
     SET @orderId=CONVERT(int,SCOPE_IDENTITY());
     INSERT dbo.OrderLines(StoreId,OrderId,ProductId,ProductName,Quantity,UnitPrice,LineTotal,RawCustomerText,AiNote,IsUnresolved)
     SELECT @storeId,@orderId,l.ProductId,p.Name,l.Quantity,CONVERT(decimal(18,2),l.PriceCents)/100,
      CONVERT(decimal(18,2),CONVERT(bigint,l.Quantity)*l.PriceCents)/100,N'',N'',0
      FROM @lines l JOIN dbo.Products p ON p.Id=l.ProductId AND p.StoreId=@storeId;
    END;
    SET @candidate=JSON_MODIFY(@candidate,'$.status',N'submitted');
    UPDATE dbo.BotSessions SET CartJson=@candidate,OrderId=@orderId WHERE SessionId=@sessionId;
    UPDATE dbo.BotMessages SET Stage=N'cart_mutated',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    DECLARE @order nvarchar(max)=(SELECT Id id,OrderNumber orderNumber,N'NeedsReview' status,CONVERT(int,Total*100) totalCents
     FROM dbo.Orders WHERE Id=@orderId AND StoreId=@storeId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    SET @result=JSON_MODIFY(@result,'$.order',JSON_QUERY(@order));
   END;
   GOTO Emit;
  END;

  IF @operation IN(N'send.claim',N'send.finalize',N'finish')
  BEGIN
   SELECT @instance=InstanceName FROM dbo.BotWhatsAppConnections WHERE StoreId=@storeId;
   IF @instance<>ISNULL(JSON_VALUE(@input,'$.instanceName'),N'') OR @identity<>ISNULL(JSON_VALUE(@input,'$.recipient'),N'') BEGIN SET @code=N'send_target_mismatch'; GOTO Reject; END;
   SET @result=JSON_MODIFY(@result,'$.instanceName',@instance); SET @result=JSON_MODIFY(@result,'$.recipient',@identity);
   SELECT @claimId=ClaimId,@deliveryStatus=Status,@deliveryVersion=CartVersion,@saved=Reply FROM dbo.BotDeliveries
    WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   IF @operation=N'send.claim'
   BEGIN
    DECLARE @reply nvarchar(max)=JSON_VALUE(@input,'$.reply');
    IF NULLIF(@reply,N'') IS NULL OR LEN(@reply)>4000 BEGIN SET @code=N'invalid_reply'; GOTO Reject; END;
    IF @claimId IS NOT NULL AND (@saved<>@reply OR ISNULL(@deliveryVersion,0)<>ISNULL(TRY_CONVERT(int,JSON_VALUE(@input,'$.cartVersion')),0))
     BEGIN SET @code=N'delivery_content_conflict'; GOTO Reject; END;
    IF @deliveryStatus IN(N'sending',N'unknown') BEGIN SET @code=N'delivery_reconciliation_required'; GOTO Reject; END;
    IF @claimId IS NULL
    BEGIN
     SET @claimId=CONVERT(nvarchar(64),NEWID());
     INSERT dbo.BotDeliveries(StoreId,SessionId,MessageId,ClaimId,Reply,CartVersion,Status)
      VALUES(@storeId,@sessionId,@messageId,@claimId,@reply,TRY_CONVERT(int,JSON_VALUE(@input,'$.cartVersion')),N'sending');
    END
    ELSE IF @deliveryStatus=N'failed'
    BEGIN
     SET @claimId=CONVERT(nvarchar(64),NEWID());
     UPDATE dbo.BotDeliveries SET ClaimId=@claimId,Status=N'sending',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    END;
    UPDATE dbo.BotMessages SET Stage=N'prepared',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    SET @result=JSON_MODIFY(@result,'$.claimId',@claimId);
    SET @result=JSON_MODIFY(@result,'$.status',CASE WHEN @deliveryStatus=N'sent' THEN N'sent' ELSE N'claimed' END);
   END
   ELSE IF @operation=N'send.finalize'
   BEGIN
    IF @claimId IS NULL OR @claimId<>ISNULL(JSON_VALUE(@input,'$.claimId'),N'') BEGIN SET @code=N'claim_scope_mismatch'; GOTO Reject; END;
    DECLARE @nextStatus nvarchar(20)=JSON_VALUE(@input,'$.deliveryStatus'),@provider nvarchar(200)=JSON_VALUE(@input,'$.providerMessageId');
    IF @nextStatus IS NULL OR @nextStatus NOT IN(N'sent',N'failed',N'unknown') OR (@nextStatus=N'sent' AND NULLIF(@provider,N'') IS NULL) BEGIN SET @code=N'invalid_delivery_status'; GOTO Reject; END;
    IF @deliveryStatus<>N'sending' AND @deliveryStatus<>@nextStatus BEGIN SET @code=N'delivery_status_conflict'; GOTO Reject; END;
    UPDATE dbo.BotDeliveries SET Status=@nextStatus,ProviderMessageId=@provider,UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    IF @nextStatus=N'sent' AND @deliveryVersion>0 AND TRY_CONVERT(int,JSON_VALUE(@cart,'$.version'))=@deliveryVersion
     UPDATE dbo.BotSessions SET LastShownVersion=@deliveryVersion,LastShownDeliveryStatus=N'sent' WHERE SessionId=@sessionId;
    UPDATE dbo.BotMessages SET Stage=@nextStatus,UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    SET @result=JSON_MODIFY(@result,'$.claimId',@claimId); SET @result=JSON_MODIFY(@result,'$.deliveryStatus',@nextStatus);
   END
   ELSE
   BEGIN
    DECLARE @requestedStatus nvarchar(20)=JSON_VALUE(@input,'$.deliveryStatus');
    IF @requestedStatus=N'sent' AND (@deliveryStatus IS NULL OR @deliveryStatus<>N'sent') BEGIN SET @code=N'delivery_not_persisted'; GOTO Reject; END;
    IF @deliveryStatus=N'sent'
    BEGIN
     INSERT dbo.BotHistory(StoreId,SessionId,ConversationId,MessageId,Role,Content)
      SELECT @storeId,@sessionId,@conversationId,@messageId,N'user',LEFT(ISNULL(t.[text],N''),6000)
      FROM dbo.BotMessages m CROSS APPLY OPENJSON(m.InputJson) WITH([text] nvarchar(max) '$.text') t
      WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId
       AND NOT EXISTS(SELECT 1 FROM dbo.BotHistory h WHERE h.StoreId=@storeId AND h.SessionId=@sessionId AND h.MessageId=@messageId AND h.Role=N'user');
     IF NOT EXISTS(SELECT 1 FROM dbo.BotHistory WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND Role=N'assistant')
      INSERT dbo.BotHistory(StoreId,SessionId,ConversationId,MessageId,Role,Content) VALUES(@storeId,@sessionId,@conversationId,@messageId,N'assistant',@saved);
     UPDATE dbo.BotMessages SET Stage=N'completed',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
     UPDATE dbo.BotRecoveryQueue SET ResolvedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    END
    ELSE
    BEGIN
     IF @deliveryStatus=N'sending' UPDATE dbo.BotDeliveries SET Status=N'unknown',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
     UPDATE dbo.BotMessages SET Stage=CASE WHEN @deliveryStatus=N'failed' THEN N'failed' ELSE N'unknown' END,UpdatedAtUtc=@now
      WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
     IF NOT EXISTS(SELECT 1 FROM dbo.BotRecoveryQueue WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId)
      INSERT dbo.BotRecoveryQueue(StoreId,SessionId,MessageId,Reason) VALUES(@storeId,@sessionId,@messageId,N'delivery_reconciliation');
    END;
    UPDATE dbo.BotSessions SET LeaseToken=NULL,LeaseUntil=NULL,ActiveMessageId=NULL WHERE SessionId=@sessionId AND LeaseToken=@leaseToken;
   END;
   GOTO Emit;
  END;

  IF @operation=N'website.finish'
  BEGIN
   IF @channel<>N'website' BEGIN SET @code=N'wrong_channel'; GOTO Reject; END;
   DECLARE @body nvarchar(max)=JSON_QUERY(@input,'$.body'),@success bit=CASE WHEN JSON_VALUE(@input,'$.body.success')=N'true' THEN 1 ELSE 0 END;
   IF @body IS NULL OR ISNULL(JSON_VALUE(@body,'$.requestId'),N'')<>@messageId OR ISNULL(JSON_VALUE(@body,'$.conversationId'),N'')<>@conversationId
    BEGIN SET @code=N'website_response_scope_mismatch'; GOTO Reject; END;
   IF @success=1
   BEGIN
    UPDATE dbo.BotMessages SET SavedResponse=@body,Stage=N'completed',UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
    INSERT dbo.BotHistory(StoreId,SessionId,ConversationId,MessageId,Role,Content)
     SELECT @storeId,@sessionId,@conversationId,@messageId,N'user',LEFT(JSON_VALUE(InputJson,'$.text'),1500)
      FROM dbo.BotMessages WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId
       AND NOT EXISTS(SELECT 1 FROM dbo.BotHistory WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND Role=N'user');
    IF NOT EXISTS(SELECT 1 FROM dbo.BotHistory WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId AND Role=N'assistant')
     INSERT dbo.BotHistory(StoreId,SessionId,ConversationId,MessageId,Role,Content)
      VALUES(@storeId,@sessionId,@conversationId,@messageId,N'assistant',
       LEFT(CONCAT(N'catalog=',JSON_QUERY(@body,'$.catalog'),N'; products=',JSON_QUERY(@body,'$.products'),N'; reply=',JSON_VALUE(@body,'$.reply')),6000));
    UPDATE dbo.BotRecoveryQueue SET ResolvedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   END
   ELSE UPDATE dbo.BotMessages SET Stage=N'failed',SavedResponse=NULL,UpdatedAtUtc=@now WHERE StoreId=@storeId AND SessionId=@sessionId AND MessageId=@messageId;
   UPDATE dbo.BotSessions SET LeaseToken=NULL,LeaseUntil=NULL,ActiveMessageId=NULL WHERE SessionId=@sessionId AND LeaseToken=@leaseToken;
   SET @result=JSON_MODIFY(@result,'$.finished',CAST(1 AS bit)); SET @result=JSON_MODIFY(@result,'$.responseSaved',@success);
   GOTO Emit;
  END;

  BEGIN SET @code=N'unsupported_operation'; GOTO Reject; END;
  Emit:
  COMMIT TRANSACTION;
  SET @output=@result; SELECT @result AS payload; RETURN;
  Reject:
  IF @started=1
  BEGIN
   IF @outerTranCount>0 BEGIN ROLLBACK TRANSACTION CypherBotCall; COMMIT TRANSACTION; END
   ELSE ROLLBACK TRANSACTION;
  END;
  SET @result=(SELECT CAST(0 AS bit) success,CAST(0 AS bit) ok,@code code,@storeId storeId,
   @sessionId sessionId,@conversationId conversationId,@messageId messageId,@leaseToken leaseToken FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  SET @output=@result; SELECT @result AS payload; RETURN;
 END TRY
 BEGIN CATCH
  IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
  SET @code=CASE WHEN ERROR_NUMBER()=51200 THEN ERROR_MESSAGE() ELSE N'integration_unavailable' END;
  SET @result=(SELECT CAST(0 AS bit) success,CAST(0 AS bit) ok,@code code FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  SET @output=@result; SELECT @result AS payload;
 END CATCH;
END;
GO
