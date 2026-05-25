CREATE TABLE [dbo].[PlanningDocument]
(
    [Id] UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL,
    [Name] NVARCHAR(255) NOT NULL,
    [DocumentType] NVARCHAR(50) NOT NULL,
    [Url] NVARCHAR(500) NOT NULL,
    [PublishedDate] DATETIME2(7) NULL,
    [ContentText] NVARCHAR(MAX) NULL,
    [FileName] NVARCHAR(255) NULL,
    [ContentType] NVARCHAR(255) NULL,
    [ParseStatus] NVARCHAR(50) NULL,
    [ParseError] NVARCHAR(1000) NULL,
    [ParsedAt] DATETIME2(7) NULL,
    [PlanningApplicationId] UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT [FK_PlanningDocuments_PlanningApplication] FOREIGN KEY ([PlanningApplicationId]) REFERENCES [dbo].[PlanningApplication]([Id]),
    CONSTRAINT [PK_PlanningDocuments] PRIMARY KEY CLUSTERED ([Id] ASC)
);

GO

CREATE UNIQUE NONCLUSTERED INDEX [UX_PlanningDocument_Application_Url]
    ON [dbo].[PlanningDocument] ([PlanningApplicationId], [Url]);

GO

CREATE NONCLUSTERED INDEX [IX_PlanningDocument_Application_PublishedDate]
    ON [dbo].[PlanningDocument] ([PlanningApplicationId], [PublishedDate])
    WHERE [PublishedDate] IS NOT NULL;
