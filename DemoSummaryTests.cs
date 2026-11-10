using System.Text.Json;

internal static class DemoSummaryTests
{
    internal static void Run()
    {
        static JsonElement Result(object data) => JsonSerializer.SerializeToElement(new { structuredContent = data });
        var companies = Enumerable.Range(1, 5).Select(i => new
        {
            id = i.ToString(), properties = new { name = $"DEMO-{i:000}", domain = $"demo{i}.example.com" }
        }).ToArray();
        var contacts = new[] { 1, 3, 4, 5 }.Select(i => new
        {
            id = i.ToString(), properties = new { firstname = "CTO", lastname = i.ToString(),
                email = $"cto@demo{i}.example.com", jobtitle = "Chief Technology Officer" }
        }).ToArray();
        object Opportunity(string id, int company, bool closed, decimal? amount, string? currency = null) => new
        {
            Id = id, Account = new { Name = $"DEMO-{company:000}" }, IsClosed = closed, Amount = amount,
            CurrencyIsoCode = currency
        };
        var opportunities = new[] { Opportunity("a", 1, false, 24000), Opportunity("b", 2, false, 18000),
            Opportunity("c", 3, true, 32000), Opportunity("d", 4, false, 15000),
            Opportunity("e", 4, false, 45000), Opportunity("f", 4, true, 8000) };
        var results = new Dictionary<string, JsonElement>
        {
            [DemoSummary.Sources[0]] = Result(new { results = companies, total = 5 }),
            [DemoSummary.Sources[1]] = Result(new { results = contacts, total = 4 }),
            [DemoSummary.Sources[2]] = Result(new { records = opportunities, done = true, totalSize = 6 })
        };
        var report = DemoSummary.Build(results);
        if (!report.Complete || report.Companies.Length != 5 ||
            report.Companies.Count(c => c.MeetsBothConditions) != 2 ||
            report.Companies[0].OpenOpportunityValue != 24000 ||
            report.Companies[3].OpenOpportunityValue != 60000 || report.Companies[3].OpenOpportunityCount != 2 ||
            report.Companies[2].CtoContacts.Length != 1 || report.Companies[2].OpenOpportunityCount != 0 ||
            report.Companies[4].OpenOpportunityValue != 0)
            throw new Exception("Demo joins or totals failed.");

        // Another CTO at the same company must not multiply the opportunity total.
        var extra = contacts.Append(new { id = "extra", properties = new { firstname = "Second", lastname = "CTO",
            email = "second@demo4.example.com", jobtitle = "Chief Technology Officer" } }).ToArray();
        results[DemoSummary.Sources[1]] = Result(new { results = extra, total = 5 });
        if (DemoSummary.Build(results).Companies[3].OpenOpportunityValue != 60000)
            throw new Exception("Contacts multiplied pipeline value.");

        results[DemoSummary.Sources[2]] = Result(new { records = opportunities, done = false, totalSize = 7 });
        if (DemoSummary.Build(results).Complete) throw new Exception("Partial Salesforce data accepted as complete.");
        results[DemoSummary.Sources[2]] = Result(new { records = opportunities });
        if (DemoSummary.Build(results).Complete) throw new Exception("Missing completion evidence accepted.");
        results[DemoSummary.Sources[2]] = Result(new { records = new[] { Opportunity("x", 4, false, 10, "USD"),
            Opportunity("y", 4, false, 20, "GBP") }, done = true, totalSize = 2 });
        if (DemoSummary.Build(results).Companies[3].OpenOpportunityValue is not null)
            throw new Exception("Mixed currencies totalled.");
        results[DemoSummary.Sources[2]] = Result(new { records = new[] { Opportunity("x", 1, false, null) }, done = true, totalSize = 1 });
        if (DemoSummary.Build(results).Companies[0].OpenOpportunityValue is not null)
            throw new Exception("Missing amount counted as zero.");

        results[DemoSummary.Sources[2]] = Result(new { records = opportunities.Append(opportunities[0]), done = true, totalSize = 7 });
        try { DemoSummary.Build(results); throw new Exception("Duplicate opportunity accepted."); }
        catch (InvalidOperationException) { }

        results[DemoSummary.Sources[2]] = Result(new { records = opportunities, done = true, totalSize = 6 });
        results[DemoSummary.Sources[0]] = Result(new { results = companies, total = 8 });
        if (DemoSummary.Build(results).Complete) throw new Exception("Partial HubSpot data accepted as complete.");

        // The SDK may supply JSON in a text content block, including a data envelope.
        results[DemoSummary.Sources[0]] = JsonSerializer.SerializeToElement(new { content = new[]
        { new { type = "text", text = JsonSerializer.Serialize(new { data = new { results = companies, total = 5 } }) } } });
        if (!DemoSummary.Build(results).Complete) throw new Exception("JSON text envelope failed.");
        results[DemoSummary.Sources[0]] = Result(new { unexpected = companies });
        try { DemoSummary.Build(results); throw new Exception("Unknown response shape accepted."); }
        catch (InvalidOperationException) { }
        Console.WriteLine("Passed demo joins, closed-record exclusion, zero/missing amounts, mixed currencies, duplicate IDs, pagination and JSON-envelope checks.");
    }
}
