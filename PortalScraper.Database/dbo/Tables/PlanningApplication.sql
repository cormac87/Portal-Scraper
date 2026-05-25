CREATE TABLE [dbo].[PlanningApplication]
(
    [Id] UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL,
    [Title] NVARCHAR(MAX) NOT NULL,
    [ScrapedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_PlanningApplication_ScrapedAt] DEFAULT (SYSUTCDATETIME()),
    [ReceivedDate] DATETIME2(7) NULL,
    [ValidatedDate] DATETIME2(7) NULL,
    [Status] NVARCHAR(100) NULL,
    [ApplicantEmail] NVARCHAR(255) NULL,
    [ApplicantPhone] NVARCHAR(50) NULL,
    [ApplicantName] NVARCHAR(255) NULL,
    [AgentEmail] NVARCHAR(255) NULL,
    [AgentPhone] NVARCHAR(50) NULL,
    [AgentName] NVARCHAR(255) NULL,
    [CompanyName] NVARCHAR(255) NULL,
    [Address] NVARCHAR(500) NULL,
    [Description] NVARCHAR(MAX) NULL,
    [ApplicationReference] NVARCHAR(100) NULL,
    [SourceKey] NVARCHAR(100) NULL,
    [SourceUrl] NVARCHAR(500) NULL,
    [PlanningAuthorityId] UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT [PK_PlanningApplication] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [FK_PlanningApplication_PlanningAuthority] FOREIGN KEY ([PlanningAuthorityId]) REFERENCES [dbo].[PlanningAuthority]([Id])
);

GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanningApplication_Authority_Reference]
    ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ApplicationReference])
    WHERE [ApplicationReference] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_SourceKey]
    ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [SourceKey])
    WHERE [SourceKey] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_ValidatedDate]
    ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ValidatedDate])
    WHERE [ValidatedDate] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_PlanningApplication_Authority_ReceivedDate]
    ON [dbo].[PlanningApplication] ([PlanningAuthorityId], [ReceivedDate])
    WHERE [ReceivedDate] IS NOT NULL;

GO

CREATE NONCLUSTERED INDEX [IX_PlanningApplication_ScrapedAt_Reference_Id]
    ON [dbo].[PlanningApplication] ([ScrapedAt] DESC, [ApplicationReference] ASC, [Id] ASC);
