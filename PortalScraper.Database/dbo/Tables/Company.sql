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
    [NormalizedPostcode] AS (UPPER(REPLACE([RegAddressPostCode], N' ', N''))) PERSISTED,
    [NormalizedCompanyName] AS (UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL([CompanyName], N''), N'&', N'AND'), N' ', N''), N'.', N''), N',', N''), N'''', N''), N'-', N''), N'/', N''), N'(', N''), N')', N''), N':', N''), N';', N'')) PERSISTED,
    [Latitude] FLOAT NULL,
    [Longitude] FLOAT NULL,
    [Location] GEOGRAPHY NULL,
    [LocationLookupStatus] NVARCHAR(30) NULL,
    [LocationLookupMessage] NVARCHAR(255) NULL,
    [LocationLookupAtUtc] DATETIME2(7) NULL,
    CONSTRAINT [PK_Company] PRIMARY KEY CLUSTERED ([Id] ASC)
);

GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_Company_CompanyNumber]
    ON [dbo].[Company] ([CompanyNumber]);

GO

CREATE NONCLUSTERED INDEX [IX_Company_CompanyName]
    ON [dbo].[Company] ([CompanyName])
    WHERE [CompanyName] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_Company_NormalizedCompanyName]
    ON [dbo].[Company] ([NormalizedCompanyName])
    WHERE [NormalizedCompanyName] <> N'';

GO

CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText1]
    ON [dbo].[Company] ([SicCodeSicText1])
    WHERE [SicCodeSicText1] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText2]
    ON [dbo].[Company] ([SicCodeSicText2])
    WHERE [SicCodeSicText2] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText3]
    ON [dbo].[Company] ([SicCodeSicText3])
    WHERE [SicCodeSicText3] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_Company_SicCodeSicText4]
    ON [dbo].[Company] ([SicCodeSicText4])
    WHERE [SicCodeSicText4] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_Company_NormalizedPostcode]
    ON [dbo].[Company] ([NormalizedPostcode]);

GO

CREATE NONCLUSTERED INDEX [IX_Company_Latitude_Longitude]
    ON [dbo].[Company] ([Latitude], [Longitude])
    WHERE [Latitude] IS NOT NULL
        AND [Longitude] IS NOT NULL;

GO

CREATE SPATIAL INDEX [SIX_Company_Location]
    ON [dbo].[Company] ([Location])
    USING GEOGRAPHY_AUTO_GRID
    WITH (CELLS_PER_OBJECT = 16);
