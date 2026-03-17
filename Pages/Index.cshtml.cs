using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Azure;
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

    public string SelectedSeverity { get; private set; } = "All";
    public string SelectedServer { get; private set; } = "All";

    private const int LatestRunWindowMinutes = 10;

    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    // ✅ CSV EXPORT (INCLUDES ADDITIONALDETAILS)
    public async Task<IActionResult> OnGetDownloadCsvAsync()
    {
        await LoadDataAsync();

        var sb = new StringBuilder();
        sb.AppendLine("TimeGenerated,Server,Severity,RuleId,RuleName,Message,HelpLink,AdditionalDetails");

        foreach (var r in Recommendations)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.TimeGenerated.ToString("o")),
                Csv(r.ServerName),
                Csv(r.Severity),
                Csv(r.RuleId),
                Csv(r.RuleName),
                Csv(r.Message),
                Csv(r.HelpLink),
                Csv(r.AdditionalDetails)
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
            SelectedSeverity = Request.Query["severity"].FirstOrDefault() ?? "All";
            SelectedServer = Request.Query["server"].FirstOrDefault() ?? "All";

            string workspaceId = "ee45cdde-1cf1-4bb9-aabd-ff94e9e15e91";
            var client = new LogsQueryClient(new DefaultAzureCredential());

            string query = @"
SqlAssessment_CL
| where TimeGenerated > ago(90d)
| order by TimeGenerated desc
| take 5000
";

            var response = await client.QueryWorkspaceAsync(workspaceId, query, QueryTimeRange.All);
            var table = response.Value.Table;

            var all = new List<BpaRecommendation>();

            foreach (var row in table.Rows)
            {
                var raw = row["RawData"]?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var time = ((DateTimeOffset)row["TimeGenerated"]).UtcDateTime;
                all.Add(ParseRawData(raw, time));
            }

            var latestRunPerServer = all
                .GroupBy(r => r.ServerName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Max(r => r.TimeGenerated)
                );

            all = all
                .Where(r =>
                    latestRunPerServer.TryGetValue(r.ServerName, out var latest) &&
                    r.TimeGenerated >= latest.AddMinutes(-LatestRunWindowMinutes))
                .ToList();

            Servers = all.Select(r => r.ServerName).Distinct().OrderBy(s => s).ToList();

            if (SelectedSeverity != "All")
                all = all.Where(r => r.Severity == SelectedSeverity).ToList();

            if (SelectedServer != "All")
                all = all.Where(r => r.ServerName == SelectedServer).ToList();

            Recommendations = all
                .OrderBy(r => SeverityRank(r.Severity))
                .ThenBy(r => r.ServerName)
                .ThenBy(r => r.RuleName)
                .ToList();

            Status = $"Query completed. Latest run window ending at {Recommendations.Max(r => r.TimeGenerated):u}. Rows returned: {Recommendations.Count}";
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
        }
    }

    private static BpaRecommendation ParseRawData(string raw, DateTime timeGenerated)
    {
        var fields = ParseCsvLine(raw);

        string ruleId = fields.ElementAtOrDefault(2) ?? "";
        string ruleName = fields.ElementAtOrDefault(3) ?? "";
        string message = fields.ElementAtOrDefault(4) ?? "";

        string serverField = fields.ElementAtOrDefault(7) ?? "";
        string serverName = NormalizeServerName(serverField.Split(':')[0]);

        string severity = MapSeverity(fields.ElementAtOrDefault(8) ?? "");

        // ✅ HelpLink (URL-based detection)
        string helpLink = fields.FirstOrDefault(f =>
            f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ) ?? "";

        // ✅ ADDITIONAL DETAILS (schema-agnostic)
        // Anything beyond the known fields that is NOT a URL is treated as detail
        var additionalParts = fields
            .Skip(9)
            .Where(f =>
                !string.IsNullOrWhiteSpace(f) &&
                !f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !f.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Trim())
            .Distinct()
            .ToList();

        string additionalDetails = string.Join(" | ", additionalParts);

        return new BpaRecommendation
        {
            TimeGenerated = timeGenerated,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = message,
            ServerName = serverName,
            Severity = severity,
            HelpLink = helpLink,
            AdditionalDetails = additionalDetails
        };
    }

    private static string NormalizeServerName(string s)
    {
        s = s.Trim().Trim('"').ToLowerInvariant();
        var dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }

    private static string MapSeverity(string raw)
    {
        return int.TryParse(raw.Trim('"'), out int sev)
            ? sev switch
            {
                30 => "High",
                20 => "Medium",
                10 => "Low",
                _ => "Information"
            }
            : "Information";
    }

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
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }

        result.Add(current.ToString());
        return result;
    }

    private static string Csv(string? s)
    {
        s ??= "";
        s = s.Replace("\"", "\"\"");
        return s.Contains(',') || s.Contains('|')
            ? $"\"{s}\""
            : s;
    }
}