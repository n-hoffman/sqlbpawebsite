using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace bpasite.Controllers;

[ApiController]
[Route("api/bpa")]
public class BpaController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;

    public BpaController(IConfiguration config, IMemoryCache cache)
    {
        _config = config;
        _cache = cache;
    }

    [HttpGet("recommendations")]
    public async Task<IActionResult> GetRecommendations()
    {
        var cacheKey = "bpa:recommendations:all";
        if (_cache.TryGetValue(cacheKey, out List<BpaUnifiedRow>? cached) && cached is not null)
            return Ok(cached);

        var settings = LoadSettings();

        var workspaces = settings.Workspaces
            .Where(w => !string.IsNullOrWhiteSpace(w.WorkspaceId))
            .ToList();

        if (workspaces.Count == 0)
            return Ok(new List<BpaUnifiedRow>());

        var query = $@"
SqlAssessment_CL
| where TimeGenerated > ago({settings.LookbackDays}d)
| order by TimeGenerated desc
| take {settings.Take}
| project TimeGenerated, RawData";

        var client = new LogsQueryClient(new DefaultAzureCredential());

        var tasks = workspaces.Select(w => QueryWorkspace(client, w, query)).ToArray();
        var resultsByWorkspace = await Task.WhenAll(tasks);

        var all = resultsByWorkspace.SelectMany(x => x).ToList();

        var unified = all
            .GroupBy(r => r.ServerName)
            .SelectMany(g =>
            {
                var lastRun = g.Max(r => r.TimeGenerated);

                return g
                    .Where(r => r.TimeGenerated >= lastRun.AddMinutes(-settings.LatestRunWindowMinutes))
                    .Select(r => new BpaUnifiedRow
                    {
                        lastRunDate = lastRun,
                        serverName = r.ServerName,
                        severity = r.Severity,
                        ruleId = r.RuleId,
                        title = r.RuleName,
                        description = r.Message,
                        helpLink = r.HelpLink,
                        additionalDetails = r.AdditionalDetails,
                        source = r.Source
                    });
            })
            .OrderBy(r => SeverityRank(r.severity))
            .ThenBy(r => r.serverName)
            .ThenBy(r => r.ruleId)
            .ToList();

        _cache.Set(cacheKey, unified, TimeSpan.FromMinutes(5));

        return Ok(unified);
    }

    [HttpPost("clearcache")]
    public IActionResult ClearCache()
    {
        _cache.Remove("bpa:recommendations:all");
        return Ok(new { cleared = true });
    }

    private async Task<List<BpaParsedRow>> QueryWorkspace(LogsQueryClient client, WorkspaceCfg ws, string query)
    {
        var output = new List<BpaParsedRow>();

        var response = await client.QueryWorkspaceAsync(ws.WorkspaceId, query, QueryTimeRange.All);
        var table = response.Value.Table;

        foreach (var row in table.Rows)
        {
            var raw = row["RawData"]?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var time = ((DateTimeOffset)row["TimeGenerated"]).UtcDateTime;

            var parsed = ParseRawData(raw, time);
            parsed.Source = ws.Name;
            output.Add(parsed);
        }

        return output;
    }

    private static BpaParsedRow ParseRawData(string raw, DateTime timeGenerated)
    {
        var fields = ParseCsvLine(raw);

        string ruleId = fields.ElementAtOrDefault(2) ?? "";
        string ruleName = fields.ElementAtOrDefault(3) ?? "";
        string message = fields.ElementAtOrDefault(4) ?? "";

        string serverField = fields.ElementAtOrDefault(7) ?? "";
        string serverName = NormalizeServerName(serverField.Split(':')[0]);

        string severity = MapSeverity(fields.ElementAtOrDefault(8) ?? "");

        string helpLink = fields.FirstOrDefault(f =>
            f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ) ?? "";

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

        return new BpaParsedRow
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

    private BpaSettings LoadSettings()
    {
        var s = new BpaSettings();
        _config.GetSection("Bpa").Bind(s);

        if (s.LatestRunWindowMinutes <= 0) s.LatestRunWindowMinutes = 10;
        if (s.LookbackDays <= 0) s.LookbackDays = 90;
        if (s.Take <= 0) s.Take = 5000;
        s.Workspaces ??= new List<WorkspaceCfg>();
        return s;
    }

    private class BpaSettings
    {
        public int LatestRunWindowMinutes { get; set; } = 10;
        public int LookbackDays { get; set; } = 90;
        public int Take { get; set; } = 5000;
        public List<WorkspaceCfg> Workspaces { get; set; } = new();
    }

    private class WorkspaceCfg
    {
        public string Name { get; set; } = "";
        public string WorkspaceId { get; set; } = "";
    }

    private class BpaParsedRow
    {
        public DateTime TimeGenerated { get; set; }
        public string ServerName { get; set; } = "";
        public string Severity { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string RuleName { get; set; } = "";
        public string Message { get; set; } = "";
        public string HelpLink { get; set; } = "";
        public string AdditionalDetails { get; set; } = "";
        public string Source { get; set; } = "";
    }

    public class BpaUnifiedRow
    {
        public DateTime lastRunDate { get; set; }
        public DateTime timeGenerated { get; set; }
        public string serverName { get; set; } = "";
        public string severity { get; set; } = "";
        public string ruleId { get; set; } = "";
        public string title { get; set; } = "";
        public string description { get; set; } = "";
        public string helpLink { get; set; } = "";
        public string additionalDetails { get; set; } = "";
        public string source { get; set; } = "";
    }
}
