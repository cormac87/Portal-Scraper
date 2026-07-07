namespace PortalScraper.Services.Planning;

public sealed class PlanningOrganisationExtractionOptions
{
    public const string SectionName = "PlanningOrganisationExtraction";

    public string? PythonPath { get; set; }

    public string? ScriptPath { get; set; }

    public string Model { get; set; } = "en_core_web_sm";

    public int TimeoutSeconds { get; set; } = 300;
}
