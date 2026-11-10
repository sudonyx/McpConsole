using System.Globalization;
using System.Text.Json;

internal sealed record DemoCompanySummary(string Company, string[] CtoContacts, int OpenOpportunityCount,
    decimal? OpenOpportunityValue, string Currency, bool MeetsBothConditions, string Exclusion);
internal sealed record DemoReport(bool Complete, string[] CompletenessNotes, DemoCompanySummary[] Companies);

internal static class DemoSummary
{
    internal static readonly string[] Sources =
        ["hubspot.demo-companies", "hubspot.demo-contacts", "salesforce.demo-opportunities"];

    internal static DemoReport Build(IReadOnlyDictionary<string, JsonElement> results)
    {
        var notes = new List<string>();
        var companies = Rows(results[Sources[0]], false, Sources[0], notes);
        var contacts = Rows(results[Sources[1]], false, Sources[1], notes);
        var opportunities = Rows(results[Sources[2]], true, Sources[2], notes);
        var companyNames = companies.Select(c => RequiredText(Properties(c), "name")).ToHashSet(StringComparer.Ordinal);
        if (companyNames.Count != companies.Length) throw new InvalidOperationException("Duplicate company names make the join ambiguous.");
        var domains = companies.Select(c => RequiredText(Properties(c), "domain").ToLowerInvariant()).ToArray();
        if (domains.Distinct().Count() != domains.Length) throw new InvalidOperationException("Shared company domains make the contact join ambiguous.");
        foreach (var opportunity in opportunities)
        {
            var account = opportunity.GetProperty("Account");
            if (!companyNames.Contains(RequiredText(account, "Name")))
                throw new InvalidOperationException("An opportunity did not match a returned company.");
            if (opportunity.GetProperty("IsClosed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Opportunity IsClosed must be a boolean.");
        }
        // Exact company names connect Salesforce accounts to HubSpot companies. Exact email domains
        // connect contacts to companies for this demo; this is not a verified employment association.
        var summary = companies.Select(company =>
        {
            var properties = Properties(company);
            var name = RequiredText(properties, "name");
            var domain = RequiredText(properties, "domain");
            var ctos = contacts.Select(Properties).Where(contact =>
            {
                var email = RequiredText(contact, "email");
                var at = email.LastIndexOf('@');
                return at >= 0 && email[(at + 1)..].Equals(domain, StringComparison.OrdinalIgnoreCase) &&
                    Text(contact, "jobtitle").Trim().Equals("Chief Technology Officer", StringComparison.OrdinalIgnoreCase);
            }).Select(c => (Text(c, "firstname") + " " + Text(c, "lastname")).Trim()).Distinct().Order().ToArray();
            var open = opportunities.Where(o => RequiredText(o.GetProperty("Account"), "Name") == name &&
                !o.GetProperty("IsClosed").GetBoolean()).ToArray();
            var currencies = open.Select(o => Text(o, "CurrencyIsoCode")).Distinct().ToArray();
            var amounts = open.Select(o => o.GetProperty("Amount")).ToArray();
            var canTotal = currencies.Length <= 1 && amounts.All(a => a.ValueKind == JsonValueKind.Number);
            decimal? total = canTotal ? amounts.Sum(a => a.GetDecimal()) : null;
            var currency = currencies.Length == 1 && currencies[0].Length > 0 ? currencies[0] : "Salesforce account currency";
            var exclusion = (ctos.Length > 0, open.Length > 0) switch
            {
                (true, true) => "None",
                (false, true) => "No CTO contact found in returned contacts",
                (true, false) => "No open opportunity found in returned opportunities",
                _ => "No CTO contact or open opportunity found in returned records"
            };
            if (!canTotal) notes.Add(name + ": total unavailable because amounts are missing or currencies differ.");
            return new DemoCompanySummary(name, ctos, open.Length, total, currency,
                ctos.Length > 0 && open.Length > 0, exclusion);
        }).OrderBy(c => c.Company, StringComparer.Ordinal).ToArray();
        return new DemoReport(notes.Count == 0, notes.ToArray(), summary);
    }

    private static JsonElement[] Rows(JsonElement result, bool salesforce, string source, List<string> notes)
    {
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException(source + " returned a tool error.");
        JsonElement payload;
        if (result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
            payload = structured;
        else
        {
            var blocks = result.GetProperty("content").EnumerateArray().Where(b => Text(b, "type") == "text").ToArray();
            if (blocks.Length != 1) throw new InvalidOperationException(source + " needs a single JSON text block or structured content.");
            using var document = JsonDocument.Parse(RequiredText(blocks[0], "text"));
            payload = document.RootElement.Clone();
        }
        // Accept explicit common JSON envelopes. Unknown response shapes fail instead of being
        // interpreted by the language model as complete business evidence.
        var rowKey = salesforce ? "records" : "results";
        if (!payload.TryGetProperty(rowKey, out var rows))
        {
            if (!payload.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty(rowKey, out rows))
                throw new InvalidOperationException(source + " response shape is not yet supported.");
            payload = data;
        }
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(source + " records must be an array.");
        var records = rows.EnumerateArray().Select(r => r.Clone()).ToArray();
        var ids = records.Select(r => RequiredText(r, salesforce ? "Id" : "id")).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidOperationException(source + " returned duplicate IDs; totals were not calculated.");
        if (salesforce)
        {
            if (!payload.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                (payload.TryGetProperty("nextRecordsUrl", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString())))
                notes.Add(source + ": completion is unconfirmed or more pages are available.");
            if (payload.TryGetProperty("totalSize", out var size) && size.GetInt32() != records.Length)
                notes.Add(source + ": returned count differs from totalSize.");
        }
        else if (!payload.TryGetProperty("total", out var total) || total.GetInt32() != records.Length ||
            (payload.TryGetProperty("paging", out var paging) && paging.ValueKind == JsonValueKind.Object && paging.TryGetProperty("next", out _)))
            notes.Add(source + ": total count is missing, differs from returned count, or more pages are available.");
        return records;
    }

    private static JsonElement Properties(JsonElement record) => record.GetProperty("properties");
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString()! : "";
    private static string RequiredText(JsonElement value, string name)
    {
        var text = Text(value, name);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Required response field is missing: " + name);
        return text;
    }

    internal static void Print(DemoReport report)
    {
        Console.WriteLine("Application-calculated demo summary | " + (report.Complete ? "complete query results" : "partial or unconfirmed query results"));
        foreach (var company in report.Companies)
            Console.WriteLine($"{company.Company} | CTO: {string.Join(", ", company.CtoContacts)} | Open: {company.OpenOpportunityCount} | " +
                $"Value: {company.OpenOpportunityValue?.ToString("0.00", CultureInfo.InvariantCulture) ?? "unavailable"} ({company.Currency}) | " +
                $"Matches: {company.MeetsBothConditions} | {company.Exclusion}");
        foreach (var note in report.CompletenessNotes) Console.WriteLine(note);
    }
}
