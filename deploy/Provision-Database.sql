:setvar DatabaseName "WTT"

USE [master];
GO

IF DB_ID(N'$(DatabaseName)') IS NULL
BEGIN
    CREATE DATABASE [$(DatabaseName)] COLLATE Latin1_General_100_CI_AS_SC;
END;
GO

ALTER DATABASE [$(DatabaseName)] SET RECOVERY SIMPLE;
ALTER DATABASE [$(DatabaseName)] SET PAGE_VERIFY CHECKSUM;
ALTER DATABASE [$(DatabaseName)] SET AUTO_CLOSE OFF;
ALTER DATABASE [$(DatabaseName)] SET AUTO_SHRINK OFF;
GO

USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'wtt_runtime')
    CREATE ROLE [wtt_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[dbo] TO [wtt_runtime];
GO

DECLARE @dataFile sysname = (SELECT TOP (1) name FROM sys.database_files WHERE type = 0 ORDER BY file_id);
DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' MODIFY FILE (NAME = ' +
    QUOTENAME(@dataFile) + N', FILEGROWTH = 128MB, MAXSIZE = 9500MB);';
EXEC sys.sp_executesql @sql;
GO
