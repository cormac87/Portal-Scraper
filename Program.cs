using Microsoft.EntityFrameworkCore;
using PortalScraper.Components;
using PortalScraper.Data;
using PortalScraper.Services;
using PortalScraper.Services.Companies;
using PortalScraper.Services.Documents;
using PortalScraper.Services.Export;
using PortalScraper.Services.Geocoding;
using PortalScraper.Services.Planning;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' was not found.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, sqlOptions =>
        sqlOptions.CommandTimeout(1080)));
builder.Services.Configure<PlanningPortalScraperOptions>(
    builder.Configuration.GetSection(PlanningPortalScraperOptions.SectionName));
builder.Services.Configure<GoogleMapsGeocodingOptions>(
    builder.Configuration.GetSection(GoogleMapsGeocodingOptions.SectionName));
builder.Services.AddScoped<IPlanningPortalScraper, PlanningPortalScraper>();
builder.Services.AddScoped<IPlanningDataService, PlanningDataService>();
builder.Services.AddScoped<IPlanningSearchService, PlanningSearchService>();
builder.Services.AddScoped<IPlanningAuthorityLocationService, PlanningAuthorityLocationService>();
builder.Services.AddScoped<IRelevantPlanningDocumentService, RelevantPlanningDocumentService>();
builder.Services.AddScoped<IPlanningApplicationExcelExportService, PlanningApplicationExcelExportService>();
builder.Services.AddScoped<IPlanningDocumentContentService, PlanningDocumentContentService>();
builder.Services.AddScoped<ICompanyDataService, CompanyDataService>();
builder.Services.AddScoped<ICompanyImportService, CompanyImportService>();
builder.Services.AddScoped<ICompanyExcelExportService, CompanyExcelExportService>();
builder.Services.AddHttpClient<IGoogleGeocodingService, GoogleGeocodingService>(client =>
{
    client.BaseAddress = new Uri("https://maps.googleapis.com/");
});
builder.Services.AddHttpClient<IPostcodeGeocodingService, PostcodesIoGeocodingService>(client =>
{
    client.BaseAddress = new Uri("https://api.postcodes.io/");
});
builder.Services.AddSingleton<PlanningDocumentTextExtractor, PdfPlanningDocumentTextExtractor>();
builder.Services.AddSingleton<PlanningDocumentTextExtractor, TextPlanningDocumentTextExtractor>();
builder.Services.AddSingleton<PlanningDocumentTextExtractor, WordPlanningDocumentTextExtractor>();
builder.Services.AddSingleton<PlanningDocumentTextExtractor, ExcelPlanningDocumentTextExtractor>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
