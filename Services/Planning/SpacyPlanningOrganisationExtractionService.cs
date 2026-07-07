using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PortalScraper.Data;

namespace PortalScraper.Services.Planning;

public sealed class SpacyPlanningOrganisationExtractionService(
    IWebHostEnvironment environment,
    IOptions<PlanningOrganisationExtractionOptions> options,
    ILogger<SpacyPlanningOrganisationExtractionService> logger) : IPlanningOrganisationExtractionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<PlanningOrganisationExtractionResult> ExtractOrganisationsAsync(
        PlanningApplication application,
        CancellationToken cancellationToken = default)
    {
        var documents = application.PlanningDocuments
            .Where(document => !string.IsNullOrWhiteSpace(document.ContentText))
            .OrderByDescending(document => document.PublishedDate)
            .ThenBy(document => document.Name)
            .Select(document => new SpacyOrganisationDocument(
                document.Id,
                document.Name,
                document.ContentText!))
            .ToList();
        var skippedDocumentCount = application.PlanningDocuments.Count - documents.Count;

        if (documents.Count == 0)
        {
            return new PlanningOrganisationExtractionResult([], 0, skippedDocumentCount, 0);
        }

        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"The spaCy extractor script was not found at '{scriptPath}'.");
        }

        var pythonPath = ResolvePythonPath();
        if (Path.IsPathRooted(pythonPath) && !File.Exists(pythonPath))
        {
            throw new InvalidOperationException($"The configured Python executable was not found at '{pythonPath}'.");
        }

        var requestJson = JsonSerializer.Serialize(new SpacyOrganisationExtractionRequest(documents), JsonOptions);
        var result = await RunExtractorAsync(pythonPath, scriptPath, requestJson, cancellationToken);

        return result with
        {
            SkippedDocumentCount = result.SkippedDocumentCount + skippedDocumentCount
        };
    }

    private async Task<PlanningOrganisationExtractionResult> RunExtractorAsync(
        string pythonPath,
        string scriptPath,
        string requestJson,
        CancellationToken cancellationToken)
    {
        var configuredOptions = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, configuredOptions.TimeoutSeconds)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = environment.ContentRootPath
            }
        };

        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.Environment["SPACY_MODEL"] = string.IsNullOrWhiteSpace(configuredOptions.Model)
            ? "en_core_web_sm"
            : configuredOptions.Model;

        logger.LogInformation("Starting spaCy organisation extraction with {PythonPath}", pythonPath);

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the spaCy extractor process.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        await process.StandardInput.WriteAsync(requestJson.AsMemory(), timeout.Token);
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"spaCy organisation extraction timed out after {configuredOptions.TimeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"spaCy organisation extraction failed: {TrimProcessText(message)}");
        }

        try
        {
            return JsonSerializer.Deserialize<PlanningOrganisationExtractionResult>(stdout, JsonOptions)
                ?? new PlanningOrganisationExtractionResult([], 0, 0, 0);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"spaCy returned an unreadable response: {TrimProcessText(stdout)}", ex);
        }
    }

    private string ResolvePythonPath()
    {
        var configuredPath = options.Value.PythonPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ResolvePath(configuredPath);
        }

        var localVenvPython = Path.Combine(environment.ContentRootPath, ".venv", "Scripts", "python.exe");
        return File.Exists(localVenvPython) ? localVenvPython : "python";
    }

    private string ResolveScriptPath()
    {
        var configuredPath = options.Value.ScriptPath;
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Services", "Nlp", "spacy_org_extractor.py")
            : ResolvePath(configuredPath);
    }

    private string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    private static string TrimProcessText(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 4000 ? trimmed : string.Concat(trimmed.AsSpan(0, 4000), "...");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record SpacyOrganisationExtractionRequest(
        IReadOnlyList<SpacyOrganisationDocument> Documents);

    private sealed record SpacyOrganisationDocument(
        Guid Id,
        string Name,
        string Text);
}
