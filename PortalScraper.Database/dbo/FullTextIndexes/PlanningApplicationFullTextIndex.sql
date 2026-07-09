CREATE FULLTEXT INDEX ON [dbo].[PlanningApplication]
(
    [Title] LANGUAGE 2057,
    [Description] LANGUAGE 2057,
    [Address] LANGUAGE 2057
)
KEY INDEX [PK_PlanningApplication]
ON [PlanningSearchCatalog]
WITH
(
    CHANGE_TRACKING = AUTO,
    STOPLIST = OFF
);
