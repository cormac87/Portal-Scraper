SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID('dbo.Company', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Company]
    (
        [Id] UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL,
        [CompanyName] NVARCHAR(512) NULL,
        [CompanyNumber] NVARCHAR(20) NOT NULL,
        [Email] NVARCHAR(255) NULL,
        [PhoneNumber] NVARCHAR(50) NULL,
        [RegAddressCareOf] NVARCHAR(255) NULL,
        [RegAddressPoBox] NVARCHAR(100) NULL,
        [RegAddressAddressLine1] NVARCHAR(255) NULL,
        [RegAddressAddressLine2] NVARCHAR(255) NULL,
        [RegAddressPostTown] NVARCHAR(255) NULL,
        [RegAddressCounty] NVARCHAR(255) NULL,
        [RegAddressCountry] NVARCHAR(255) NULL,
        [RegAddressPostCode] NVARCHAR(20) NULL,
        [NormalizedPostcode] AS (UPPER(REPLACE([RegAddressPostCode], N' ', N''))) PERSISTED,
        [Latitude] FLOAT NULL,
        [Longitude] FLOAT NULL,
        [Location] GEOGRAPHY NULL,
        [LocationLookupStatus] NVARCHAR(30) NULL,
        [LocationLookupMessage] NVARCHAR(255) NULL,
        [LocationLookupAtUtc] DATETIME2(7) NULL,
        [CompanyCategory] NVARCHAR(255) NULL,
        [CompanyStatus] NVARCHAR(100) NULL,
        [CountryOfOrigin] NVARCHAR(100) NULL,
        [DissolutionDate] NVARCHAR(20) NULL,
        [IncorporationDate] NVARCHAR(20) NULL,
        [AccountsAccountRefDay] NVARCHAR(2) NULL,
        [AccountsAccountRefMonth] NVARCHAR(2) NULL,
        [AccountsNextDueDate] NVARCHAR(20) NULL,
        [AccountsLastMadeUpDate] NVARCHAR(20) NULL,
        [AccountsAccountCategory] NVARCHAR(100) NULL,
        [ReturnsNextDueDate] NVARCHAR(20) NULL,
        [ReturnsLastMadeUpDate] NVARCHAR(20) NULL,
        [MortgagesNumMortCharges] NVARCHAR(20) NULL,
        [MortgagesNumMortOutstanding] NVARCHAR(20) NULL,
        [MortgagesNumMortPartSatisfied] NVARCHAR(20) NULL,
        [MortgagesNumMortSatisfied] NVARCHAR(20) NULL,
        [SicCodeSicText1] NVARCHAR(255) NULL,
        [SicCodeSicText2] NVARCHAR(255) NULL,
        [SicCodeSicText3] NVARCHAR(255) NULL,
        [SicCodeSicText4] NVARCHAR(255) NULL,
        [LimitedPartnershipsNumGenPartners] NVARCHAR(20) NULL,
        [LimitedPartnershipsNumLimPartners] NVARCHAR(20) NULL,
        [Uri] NVARCHAR(500) NULL,
        [PreviousName1ConDate] NVARCHAR(20) NULL,
        [PreviousName1CompanyName] NVARCHAR(512) NULL,
        [PreviousName2ConDate] NVARCHAR(20) NULL,
        [PreviousName2CompanyName] NVARCHAR(512) NULL,
        [PreviousName3ConDate] NVARCHAR(20) NULL,
        [PreviousName3CompanyName] NVARCHAR(512) NULL,
        [PreviousName4ConDate] NVARCHAR(20) NULL,
        [PreviousName4CompanyName] NVARCHAR(512) NULL,
        [PreviousName5ConDate] NVARCHAR(20) NULL,
        [PreviousName5CompanyName] NVARCHAR(512) NULL,
        [PreviousName6ConDate] NVARCHAR(20) NULL,
        [PreviousName6CompanyName] NVARCHAR(512) NULL,
        [PreviousName7ConDate] NVARCHAR(20) NULL,
        [PreviousName7CompanyName] NVARCHAR(512) NULL,
        [PreviousName8ConDate] NVARCHAR(20) NULL,
        [PreviousName8CompanyName] NVARCHAR(512) NULL,
        [PreviousName9ConDate] NVARCHAR(20) NULL,
        [PreviousName9CompanyName] NVARCHAR(512) NULL,
        [PreviousName10ConDate] NVARCHAR(20) NULL,
        [PreviousName10CompanyName] NVARCHAR(512) NULL,
        [ConfStmtNextDueDate] NVARCHAR(20) NULL,
        [ConfStmtLastMadeUpDate] NVARCHAR(20) NULL,
        [ImportedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_Company_ImportedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_Company] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_Company_CompanyNumber'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX [UX_Company_CompanyNumber]
        ON [dbo].[Company] ([CompanyNumber]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_CompanyName'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_CompanyName]
        ON [dbo].[Company] ([CompanyName])
        WHERE [CompanyName] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_SicCodeSicText1'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText1]
        ON [dbo].[Company] ([SicCodeSicText1])
        WHERE [SicCodeSicText1] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_SicCodeSicText2'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText2]
        ON [dbo].[Company] ([SicCodeSicText2])
        WHERE [SicCodeSicText2] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_SicCodeSicText3'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText3]
        ON [dbo].[Company] ([SicCodeSicText3])
        WHERE [SicCodeSicText3] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_SicCodeSicText4'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText4]
        ON [dbo].[Company] ([SicCodeSicText4])
        WHERE [SicCodeSicText4] IS NOT NULL;
END;

IF COL_LENGTH('dbo.Company', 'NormalizedPostcode') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [NormalizedPostcode] AS (UPPER(REPLACE([RegAddressPostCode], N' ', N''))) PERSISTED;
END;

IF COL_LENGTH('dbo.Company', 'Latitude') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [Latitude] FLOAT NULL;
END;

IF COL_LENGTH('dbo.Company', 'Longitude') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [Longitude] FLOAT NULL;
END;

IF COL_LENGTH('dbo.Company', 'Location') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [Location] GEOGRAPHY NULL;
END;

IF COL_LENGTH('dbo.Company', 'LocationLookupStatus') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [LocationLookupStatus] NVARCHAR(30) NULL;
END;

IF COL_LENGTH('dbo.Company', 'LocationLookupMessage') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [LocationLookupMessage] NVARCHAR(255) NULL;
END;

IF COL_LENGTH('dbo.Company', 'LocationLookupAtUtc') IS NULL
BEGIN
    ALTER TABLE [dbo].[Company]
        ADD [LocationLookupAtUtc] DATETIME2(7) NULL;
END;

GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_NormalizedPostcode'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_NormalizedPostcode]
        ON [dbo].[Company] ([NormalizedPostcode]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Company_Latitude_Longitude'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Company_Latitude_Longitude]
        ON [dbo].[Company] ([Latitude], [Longitude])
        WHERE [Latitude] IS NOT NULL
            AND [Longitude] IS NOT NULL;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'SIX_Company_Location'
        AND object_id = OBJECT_ID('dbo.Company')
)
BEGIN
    CREATE SPATIAL INDEX [SIX_Company_Location]
        ON [dbo].[Company] ([Location])
        USING GEOGRAPHY_AUTO_GRID
        WITH (CELLS_PER_OBJECT = 16);
END;

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

IF COL_LENGTH('dbo.PlanningAuthority', 'Latitude') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningAuthority]
        ADD [Latitude] FLOAT NULL;
END;

IF COL_LENGTH('dbo.PlanningAuthority', 'Longitude') IS NULL
BEGIN
    ALTER TABLE [dbo].[PlanningAuthority]
        ADD [Longitude] FLOAT NULL;
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

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PlanningAuthority_Location'
        AND object_id = OBJECT_ID('dbo.PlanningAuthority')
)
BEGIN
    EXEC(N'
        CREATE NONCLUSTERED INDEX [IX_PlanningAuthority_Location]
            ON [dbo].[PlanningAuthority] ([Latitude], [Longitude])
            WHERE [Latitude] IS NOT NULL AND [Longitude] IS NOT NULL;');
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
        WHERE object_id = OBJECT_ID('dbo.Company')
    )
BEGIN
    EXEC(N'
        CREATE FULLTEXT INDEX ON [dbo].[Company]
        (
            [CompanyName] LANGUAGE 2057
        )
        KEY INDEX [PK_Company]
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
