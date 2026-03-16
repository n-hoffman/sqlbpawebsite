namespace bpasite.Models;

public class BpaRecommendation
{
    public DateTime TimeGenerated { get; set; }

    // Optional but useful for debugging/auditing
    public string AssessmentRunId { get; set; } = "";

    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public string Message { get; set; } = "";

    public string Severity { get; set; } = "Unknown";

    // ✅ NEW: required for UI + CSV + filtering
    public string ServerName { get; set; } = "Unknown";
}