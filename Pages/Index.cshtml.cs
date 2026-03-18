using System.Text;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using bpasite.Models;

namespace bpasite.Pages;

public class IndexModel : PageModel
{
    public string? Status { get; private set; }
    public string? Error { get; private set; }

    public List<BpaRecommendation> Recommendations { get; private set; } = new();
    public List<string> Servers { get; private set; } = new();

    // Filters
    public List<string> SourceTypes { get; } = new() { "All", "Arc", "AzureVM" };
    public string SelectedSourceType { get; private set; } = "All";
    public string SelectedServer { get; private set; } = "All";

    // ✅ Multi-select severity
    public List<string> SelectedSeverities { get; private set; } = new();

    private const int LatestRunWindowMinutes = 10;

    private const string PrimaryWorkspaceId = "ee45cdde-1cf1-4bb9-aabd-ff94e9e15e91";
    private const string SecondWorkspaceId  = "c35180b0-3a49-406b-b1f0-fd874bd61dd2";

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnGetDownloadCsvAsync()
    {
        await LoadDataAsync();

        var sb = new StringBuilder();
        sb.AppendLine("TimeGenerated,Server,SourceType,Severity,RuleId,RuleName,Message,HelpLink,AdditionalDetails,ResourceId");

        foreach (var r in Recommendations)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.TimeGenerated.ToString("o")),
                Csv(r.ServerName),
                Csv(r.SourceType),
                Csv(r.Severity),
                Csv(r.RuleId),
                Csv(r.RuleName),
                Csv(r.Message),
                Csv(r.HelpLink),
                Csv(r.AdditionalDetails),
                Csv(r.ResourceId)
            ));
        }

        return File(
            Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv",
            $"sql-bpa-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"
        );
    }

    private async Task LoadDataAsync()
    {
        try
        {
            SelectedSourceType = Request.Query["source"].FirstOrDefault() ?? "All";
            SelectedServer     = Request.Query["server"].FirstOrDefault() ?? "All";

            // ✅ FIX: normalize nullable → non-null
            SelectedSeverities = Request.Query["severity"]
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var client = new LogsQueryClient(new DefaultAzureCredential());
            var response = await client.QueryWorkspaceAsync(
                PrimaryWorkspaceId,
                BuildKqlQuery(),
                QueryTimeRange.All);

            var table = response.Value.Table;
            var all = new List<BpaRecommendation>();
            int skipped = 0;

            foreach (var row in table.Rows)
            {
                try
                {
                    var raw = row["RawData"]?.ToString();
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    var time = ((DateTimeOffset)row["TimeGenerated"]).UtcDateTime;
                    var resourceId = row["ResourceId"]?.ToString() ?? "";

                    var rec = ParseRawData(raw, time, resourceId);
                    if (!string.IsNullOrWhiteSpace(rec.ServerName))
                        all.Add(rec);
                }
                catch
                {
                    skipped++;
                }
            }

            Servers = all
                .Select(r => r.ServerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            if (SelectedSourceType != "All")
                all = all.Where(r => r.SourceType == SelectedSourceType).ToList();

            if (SelectedServer != "All")
                all = all.Where(r => r.ServerName == SelectedServer).ToList();

            if (SelectedSeverities.Any())
                all = all.Where(r => SelectedSeverities.Contains(r.Severity)).ToList();

            Recommendations = all
                .OrderBy(r => SeverityRank(r.Severity))
                .ThenBy(r => r.SourceType)
                .ThenBy(r => r.ServerName)
                .ThenBy(r => r.RuleName)
                .ToList();

            Status = Recommendations.Any()
                ? $"Query completed (2 workspaces union). Latest run window ending at {Recommendations.Max(r => r.TimeGenerated):u}. Rows returned: {Recommendations.Count}. Skipped rows: {skipped}."
                : $"Query completed. Rows returned: 0. Skipped rows: {skipped}.";
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
        }
    }

    private static string BuildKqlQuery() => $@"
let base =
    union isfuzzy=true
        (SqlAssessment_CL),
        (workspace(""{SecondWorkspaceId}"").SqlAssessment_CL)
    | where TimeGenerated > ago(90d)
    | extend ResId = tostring(column_ifexists(""ResourceId"", column_ifexists(""_ResourceId"", """")))
    | project TimeGenerated, RawData, ResourceId=ResId;

let latestPerResource =
    base
    | summarize Latest=max(TimeGenerated) by ResourceId;

base
| lookup latestPerResource on ResourceId
| where TimeGenerated >= Latest - {LatestRunWindowMinutes}m
| project TimeGenerated, RawData, ResourceId
| order by TimeGenerated desc
";

    private static BpaRecommendation ParseRawData(string raw, DateTime timeGenerated, string resourceId)
    {
        var fields = ParseCsvLine(raw);

        string ruleId   = fields.ElementAtOrDefault(2) ?? "";
        string ruleName = fields.ElementAtOrDefault(3) ?? "";
        string message  = fields.ElementAtOrDefault(4) ?? "";

        string server = NormalizeServerName(GetLastIdSegment(resourceId));
        string severity = MapSeverity(fields.ElementAtOrDefault(8) ?? "");

        string helpLink = fields.FirstOrDefault(f =>
            f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? "";

        var details = fields
            .Skip(9)
            .Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith("http"))
            .Distinct()
            .ToList();

        return new BpaRecommendation
        {
            TimeGenerated = timeGenerated,
            ServerName = server,
            ResourceId = resourceId,
            SourceType = DetectSourceType(resourceId),
            Severity = severity,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = message,
            HelpLink = helpLink,
            AdditionalDetails = string.Join(" | ", details)
        };
    }

    private static string DetectSourceType(string id) =>
        id.Contains("/microsoft.hybridcompute/", StringComparison.OrdinalIgnoreCase) ? "Arc" :
        id.Contains("/microsoft.compute/virtualmachines/", StringComparison.OrdinalIgnoreCase) ? "AzureVM" :
        "Unknown";

    private static string GetLastIdSegment(string id) =>
        id.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";

    private static string NormalizeServerName(string s) =>
        s.Split('.', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

    private static string MapSeverity(string raw) =>
        int.TryParse(raw.Trim('"'), out var sev) ? sev switch
        {
            30 => "High",
            20 => "Medium",
            10 => "Low",
            _  => "Information"
        } : "Information";

    private static int SeverityRank(string s) => s switch
    {
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        _ => 4
    };

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }

    private static string Csv(string? s)
    {
        s ??= "";
        s = s.Replace("\"", "\"\"");
        return s.Contains(',') || s.Contains('\n') ? $"\"{s}\"" : s;
    }
}