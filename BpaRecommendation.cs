namespace bpasite.Models;

public class BpaRecommendation
{
    public DateTime TimeGenerated { get; set; }

    // Identity / grouping
    public string ServerName { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string SourceType { get; set; } = ""; // "Arc" or "AzureVM"

    // BPA fields
    public string Severity { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Message { get; set; } = "";

    // Enrichment
    public string HelpLink { get; set; } = "";
    public string AdditionalDetails { get; set; } = "";
}