SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

IF COL_LENGTH('dbo.PlanningApplication', 'ReceivedDate') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [ReceivedDate] DATETIME2(7) NULL;
END;

IF COL_LENGTH('dbo.PlanningApplication', 'ValidatedDate') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [ValidatedDate] DATETIME2(7) NULL;
END;

IF COL_LENGTH('dbo.PlanningApplication', 'Status') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [Status] NVARCHAR(100) NULL;
END;

IF COL_LENGTH('dbo.PlanningApplication', 'CompanyName') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [CompanyName] NVARCHAR(255) NULL;
END;

IF COL_LENGTH('dbo.PlanningApplication', 'SourceKey') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [SourceKey] NVARCHAR(100) NULL;
END;

IF COL_LENGTH('dbo.PlanningApplication', 'SourceUrl') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningApplication]
        ADD [SourceUrl] NVARCHAR(500) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'PublishedDate') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [PublishedDate] DATETIME2(7) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'ContentText') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [ContentText] NVARCHAR(MAX) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'FileName') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [FileName] NVARCHAR(255) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'ContentType') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [ContentType] NVARCHAR(255) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'ParseStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [ParseStatus] NVARCHAR(50) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'ParseError') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [ParseError] NVARCHAR(1000) NULL;
END;

IF COL_LENGTH('dbo.PlanningDocument', 'ParsedAt') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningDocument]
        ADD [ParsedAt] DATETIME2(7) NULL;
END;

IF COL_LENGTH('dbo.PlanningAuthority', 'Website') IS NOT NULL
    AND EXISTS (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID('dbo.PlanningAuthority')
            AND name = 'Website'
            AND max_length < 1000
    )
BEGIN
    ALTER TABLE [dbo].[PlanningAuthority]
        ALTER COLUMN [Website] NVARCHAR(500) NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_PlanningApplication_Authority_Reference'
        AND object_id = OBJECT_ID('dbo.PlanningApplication')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanningApplication_Authority_Reference]
        ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ApplicationReference])
        WHERE [ApplicationReference] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningApplication_Authority_SourceKey'
        AND object_id = OBJECT_ID('dbo.PlanningApplication')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_SourceKey]
        ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [SourceKey])
        WHERE [SourceKey] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_PlanningDocument_Application_Url'
        AND object_id = OBJECT_ID('dbo.PlanningDocument')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanningDocument_Application_Url]
        ON [dbo].[PlanningDocument] ([PlanningApplicationId], [Url]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningApplication_Authority_ValidatedDate'
        AND object_id = OBJECT_ID('dbo.PlanningApplication')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_ValidatedDate]
        ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ValidatedDate])
        WHERE [ValidatedDate] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningApplication_Authority_ReceivedDate'
        AND object_id = OBJECT_ID('dbo.PlanningApplication')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_ReceivedDate]
        ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ReceivedDate])
        WHERE [ReceivedDate] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningApplication_ScrapedAt_Reference_Id'
        AND object_id = OBJECT_ID('dbo.PlanningApplication')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningApplication_ScrapedAt_Reference_Id]
        ON [dbo].[PlanningApplication] ([ScrapedAt] DESC, [ApplicationReference] ASC, [Id] ASC);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningDocument_Application_PublishedDate'
        AND object_id = OBJECT_ID('dbo.PlanningDocument')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningDocument_Application_PublishedDate]
        ON [dbo].[PlanningDocument] ([PlanningApplicationId], [PublishedDate])
        WHERE [PublishedDate] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningAuthority_Name'
        AND object_id = OBJECT_ID('dbo.PlanningAuthority')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_PlanningAuthority_Name]
        ON [dbo].[PlanningAuthority] ([Name]);
END;

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
    AND NOT EXISTS (
        SELECT 1
        FROM sys.fulltext_catalogs
        WHERE name = 'PlanningSearchCatalog'
    )
BEGIN
    EXEC(N'CREATE FULLTEXT CATALOG [PlanningSearchCatalog] WITH ACCENT_SENSITIVITY = OFF AS DEFAULT;');
END;

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
    AND EXISTS (
        SELECT 1
        FROM sys.fulltext_catalogs
        WHERE name = 'PlanningSearchCatalog'
    )
    AND NOT EXISTS (
        SELECT 1
        FROM sys.fulltext_indexes
        WHERE object_id = OBJECT_ID('dbo.PlanningApplication')
    )
BEGIN
    EXEC(N'
        CREATE FULLTEXT INDEX ON [dbo].[PlanningApplication]
        (
            [Title] LANGUAGE 2057,
            [Description] LANGUAGE 2057,
            [Address] LANGUAGE 2057
        )
        KEY INDEX [PK_PlanningApplication]
        ON [PlanningSearchCatalog]
        WITH CHANGE_TRACKING AUTO;');
END;

IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
    AND EXISTS (
        SELECT 1
        FROM sys.fulltext_catalogs
        WHERE name = 'PlanningSearchCatalog'
    )
    AND NOT EXISTS (
        SELECT 1
        FROM sys.fulltext_indexes
        WHERE object_id = OBJECT_ID('dbo.PlanningDocument')
    )
BEGIN
    EXEC(N'
        CREATE FULLTEXT INDEX ON [dbo].[PlanningDocument]
        (
            [Name] LANGUAGE 2057,
            [DocumentType] LANGUAGE 2057,
            [ContentText] LANGUAGE 2057
        )
        KEY INDEX [PK_PlanningDocuments]
        ON [PlanningSearchCatalog]
        WITH CHANGE_TRACKING AUTO;');
END;
