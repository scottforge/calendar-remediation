namespace CalendarRemediation.Host.Agent;

// Selection for feasibility evaluation only. This does not enable model transmission.
public static class ProvisionalModelDecision
{
    public const string Provider = "OpenAI API";
    public const string Endpoint = "https://api.openai.com/v1/responses";
    public const string Model = "gpt-4.1-2025-04-14";
    public const string Authentication = "Backend API key via process environment OPENAI_API_KEY, never browser configuration";
    public const string StructuredOutput = "Responses API text.format json_schema with strict=true and docs/phase-0/model-output.schema.json";
    public const string PrivacyStatus = "UNAPPROVED. Remote processing. Require current account-specific logging, training, retention, residency and deletion terms with named human approval before real calendar transmission.";
}
