CREATE FULLTEXT INDEX ON [dbo].[Company]
(
    [CompanyName] LANGUAGE 2057
)
KEY INDEX [PK_Company]
ON [PlanningSearchCatalog]
WITH CHANGE_TRACKING AUTO;
