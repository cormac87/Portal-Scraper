CREATE TABLE [dbo].[PlanningAuthority]
(
    [Id] UNIQUEIDENTIFIER DEFAULT NEWID() NOT NULL,
    [Name] NVARCHAR(255) NOT NULL,
    [Website] NVARCHAR(500) NULL,
    CONSTRAINT [PK_PlanningAuthority] PRIMARY KEY CLUSTERED ([Id] ASC)
);

GO

CREATE NONCLUSTERED INDEX [IX_PlanningAuthority_Name]
    ON [dbo].[PlanningAuthority] ([Name]);
