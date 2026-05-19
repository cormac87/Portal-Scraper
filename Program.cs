using Microsoft.EntityFrameworkCore;
using PortalScraper.Components;
using PortalScraper.Data;
using PortalScraper.Services;
using PortalScraper.Services.Documents;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("Connection string 'Database' was not found.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddScoped<IPlanningPortalScraper, PlanningPortalScraper>();
builder.Services.AddScoped<IPlanningDocumentContentService, PlanningDocumentContentService>();
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
