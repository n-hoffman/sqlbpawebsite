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

    // ---------------- PAGE VIEW ----------------
    public async Task OnGetAsync()
    {
        await LoadDataAsync();
    }

    // ---------------- CSV EXPORT ----------------
    // URL: /Index?handler=DownloadCsv&severity=High&server=All
    public async Task<IActionResult> OnGetDownloadCsvAsync()
    {
        await LoadDataAsync();

        var sb = new StringBuilder();
        sb.AppendLine("TimeGenerated,Server,Severity,RuleId,RuleName,Message");

        foreach (var r in Recommendations)
        {
            sb.AppendLine(string.Join(",",
                Csv(r.TimeGenerated.ToString("o")),
                Csv(r.ServerName),
                Csv(r.Severity),
                Csv(r.RuleId),
                Csv(r.RuleName),
                Csv(r.Message)
            ));
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var fileName = $"sql-bpa-{SelectedServer}-{SelectedSeverity}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"
            .Replace(" ", "_");

        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    // ---------------- SHARED LOADER ----------------
    private async Task LoadDataAsync()
    {
        try
        {
            SelectedSeverity = Request.Query["severity"].FirstOrDefault() ?? "All";
            SelectedServer = Request.Query["server"].FirstOrDefault() ?? "All";

            // ✅ MUST be Log Analytics Workspace "Workspace ID / CustomerId" GUID
            string workspaceId = "ee45cdde-1cf1-4bb9-aabd-ff94e9e15e91";

            if (!Guid.TryParse(workspaceId, out _))
            {
                Status = "Query failed (invalid workspace id).";
                Error = "workspaceId must be the Log Analytics Workspace 'Workspace ID / CustomerId' (GUID), not the Resource ID.";
                Recommendations = new();
                Servers = new();
                return;
            }

            var client = new LogsQueryClient(new DefaultAzureCredential());

            // Pull enough rows to populate server list and filters
            string query = "SqlAssessment_CL | take 2000";

            Response<LogsQueryResult> response =
                await client.QueryWorkspaceAsync(workspaceId, query, QueryTimeRange.All);

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

            // Build server list before filtering
            Servers = all
                .Select(r => r.ServerName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            // Apply severity filter
            if (!SelectedSeverity.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                all = all
                    .Where(r => r.Severity.Equals(SelectedSeverity, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // Apply server filter
            if (!SelectedServer.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                all = all
                    .Where(r => r.ServerName.Equals(SelectedServer, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            Recommendations = all;
            Status = $"Query completed. Rows returned: {Recommendations.Count}";
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.ToString();
            Status = "Query failed (see Error).";
            Recommendations = new();
            Servers = new();
        }
    }

    // ---------------- BPA RAWDATA PARSING ----------------
    // Matches known-good KQL indexing:
    // RawData[2]=RuleId, [3]=FullRuleName, [4]=Description/Message, [6]=Scope, [7]=Server:Database, [8]=Severity (30/20/10/0/-1)
    // [1](https://teams.microsoft.com/l/message/19:meeting_YzFjYjczYmEtYThhNy00YTJhLWEzMzItMTIzYWNhYWYzNDJi@thread.v2/1738593309535?context=%7B%22contextType%22:%22chat%22%7D)
    private static BpaRecommendation ParseRawData(string raw, DateTime timeGenerated)
    {
        var fields = ParseCsvLine(raw);

        string ruleId = fields.Count > 2 ? fields[2] : "";
        string ruleName = fields.Count > 3 ? fields[3] : "";
        string message = fields.Count > 4 ? fields[4] : "";

        // Server is stored in fields[7] as "Server:Database" (database may be blank for server-scoped items)
        string serverField = fields.Count > 7 ? fields[7] : "";
        string serverName = ExtractServerName(serverField);

        // Severity is stored in fields[8] as numeric (30/20/10/0/-1)
        string severity = fields.Count > 8 ? MapSeverity(fields[8]) : "Information";

        return new BpaRecommendation
        {
            TimeGenerated = timeGenerated,
            RuleId = ruleId,
            RuleName = ruleName,
            Message = message,
            ServerName = string.IsNullOrWhiteSpace(serverName) ? "Unknown" : serverName,
            Severity = severity
        };
    }

    private static string ExtractServerName(string serverField)
    {
        if (string.IsNullOrWhiteSpace(serverField))
            return "";

        // KQL uses split(RawData[7], ":")[0]
        // [1](https://teams.microsoft.com/l/message/19:meeting_YzFjYjczYmEtYThhNy00YTJhLWEzMzItMTIzYWNhYWYzNDJi@thread.v2/1738593309535?context=%7B%22contextType%22:%22chat%22%7D)
        var parts = serverField.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[0].Trim('"') : serverField.Trim('"');
    }

    private static string MapSeverity(string rawSeverity)
    {
        // KQL mapping:
        // 30=High, 20=Medium, 10=Low, 0=Info, -1=Ignore → treat as Info
        // [1](https://teams.microsoft.com/l/message/19:meeting_YzFjYjczYmEtYThhNy00YTJhLWEzMzItMTIzYWNhYWYzNDJi@thread.v2/1738593309535?context=%7B%22contextType%22:%22chat%22%7D)
        if (!int.TryParse(rawSeverity.Trim('"'), out int sev))
            return "Information";

        return sev switch
        {
            30 => "High",
            20 => "Medium",
            10 => "Low",
            0 => "Information",
            -1 => "Information",
            _ => "Information"
        };
    }

    // Quote-aware CSV parser for a single line
    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '"')
            {
                // Escaped quote ("")
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    // CSV escaping for export
    private static string Csv(string? value)
    {
        value ??= "";
        value = value.Replace("\"", "\"\"");
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value}\"";
        return value;
    }
}
