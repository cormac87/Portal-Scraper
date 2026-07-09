CREATE FULLTEXT INDEX ON [dbo].[PlanningDocument]
(
    [Name] LANGUAGE 2057,
    [DocumentType] LANGUAGE 2057,
    [ContentText] LANGUAGE 2057
)
KEY INDEX [PK_PlanningDocuments]
ON [PlanningSearchCatalog]
WITH
(
    CHANGE_TRACKING = AUTO,
    STOPLIST = OFF
);
