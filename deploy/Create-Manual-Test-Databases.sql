USE [master];
GO

IF DB_ID(N'WTT_Central_Test') IS NULL
    CREATE DATABASE [WTT_Central_Test] COLLATE Latin1_General_100_CI_AS_SC;
GO

ALTER DATABASE [WTT_Central_Test] SET RECOVERY SIMPLE;
ALTER DATABASE [WTT_Central_Test] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [WTT_Central_Test] SET AUTO_CLOSE OFF;
ALTER DATABASE [WTT_Central_Test] SET AUTO_SHRINK OFF;
GO

USE [WTT_Central_Test];
GO

IF OBJECT_ID(N'dbo.ProductionContext', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ProductionContext
    (
        Id int NOT NULL CONSTRAINT PK_ProductionContext PRIMARY KEY,
        BatchNo nvarchar(50) NOT NULL,
        Shift nvarchar(20) NOT NULL,
        Recipe nvarchar(100) NOT NULL,
        UpdatedAtUtc datetime2(3) NOT NULL
    );
END;
GO

MERGE dbo.ProductionContext AS target
USING (SELECT 1 AS Id) AS source ON source.Id = target.Id
WHEN MATCHED THEN UPDATE SET BatchNo=N'BATCH-TEST-001', Shift=N'A', Recipe=N'MOCK-RECIPE', UpdatedAtUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Id, BatchNo, Shift, Recipe, UpdatedAtUtc)
VALUES (1, N'BATCH-TEST-001', N'A', N'MOCK-RECIPE', SYSUTCDATETIME());
GO

SELECT DB_NAME() AS TestDatabase, * FROM dbo.ProductionContext;
GO
