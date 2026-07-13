using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossEF.Tests;

public class CrossQueryTests(TwoDatabaseFixture fixture) : IClassFixture<TwoDatabaseFixture>
{
    [Fact]
    public async Task Join_across_two_contexts_with_marker()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          select new { c1.Id, CrmName = c1.Name, BillingName = c2.Name })
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal([2, 3], rows.Select(r => r.Id).Order().ToArray());
        Assert.All(rows, r => Assert.Equal(r.CrmName, r.BillingName));
    }

    [Fact]
    public async Task Join_across_two_contexts_without_marker_via_ToCrossListAsync()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          select new { c1, c2 })
            .ToCrossListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal([2, 3], rows.Select(r => r.c1.Id).Order().ToArray());
    }

    [Fact]
    public async Task Where_before_marker_is_pushed_to_sql_and_join_still_works()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers.Where(c => c.Country == "IT").AsCrossQuery()
                          join invoice in billing.Invoices on c1.Id equals invoice.CustomerId
                          select new { c1.Name, invoice.Amount })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Carla", row.Name);
        Assert.Equal(60m, row.Amount);
    }

    [Fact]
    public async Task Where_after_join_runs_in_memory()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          where c2.Name.StartsWith("C")
                          select new { c1.Name })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Carla", row.Name);
    }

    [Fact]
    public async Task CountAsync_across_two_contexts()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var count = await (from c1 in crm.Customers.AsCrossQuery()
                           join c2 in billing.Customers on c1.Id equals c2.Id
                           select c1.Id)
            .CountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Same_context_join_passes_through_to_ef()
    {
        await using var crm = fixture.CreateCrm();

        var rows = await (from c in crm.Customers.AsCrossQuery()
                          join o in crm.Orders on c.Id equals o.CustomerId
                          select new { c.Name, o.Total })
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Name == "Alice" && r.Total == 100m);
        Assert.Contains(rows, r => r.Name == "Bob" && r.Total == 250m);
    }

    [Fact]
    public async Task Left_join_across_contexts_falls_back_to_full_materialization()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id into matches
                          from c2 in matches.DefaultIfEmpty()
                          select new { c1.Name, BillingName = c2 != null ? c2.Name : null })
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Name == "Alice" && r.BillingName == null);
        Assert.Contains(rows, r => r.Name == "Bob" && r.BillingName == "Bob");
        Assert.Contains(rows, r => r.Name == "Carla" && r.BillingName == "Carla");
    }

    [Fact]
    public async Task Semi_join_narrows_the_inner_query_with_where_in()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.BillingSqlLog)
            before = fixture.BillingSqlLog.Count;

        _ = await (from c1 in crm.Customers.AsCrossQuery()
                   join c2 in billing.Customers on c1.Id equals c2.Id
                   select new { c1.Id })
            .ToListAsync();

        List<string> newCommands;
        lock (fixture.BillingSqlLog)
            newCommands = fixture.BillingSqlLog.Skip(before).ToList();

        var customersQuery = Assert.Single(newCommands, c => c.Contains("Customers"));
        Assert.Contains("IN", customersQuery);
    }

    [Fact]
    public async Task Where_after_join_on_outer_side_is_pushed_to_outer_sql()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.CrmSqlLog)
            before = fixture.CrmSqlLog.Count;

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          where c1.Country == "IT"
                          select new { c1.Name })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Carla", row.Name);

        List<string> commands;
        lock (fixture.CrmSqlLog)
            commands = fixture.CrmSqlLog.Skip(before).ToList();
        var crmQuery = Assert.Single(commands, c => c.Contains("Customers"));
        Assert.Contains("WHERE", crmQuery); // the country filter ran as SQL, not in memory
    }

    [Fact]
    public async Task Where_after_join_on_inner_side_is_pushed_to_inner_sql()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.BillingSqlLog)
            before = fixture.BillingSqlLog.Count;

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          where c2.Country == "DE"
                          select new { c2.Name })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Bob", row.Name);

        List<string> commands;
        lock (fixture.BillingSqlLog)
            commands = fixture.BillingSqlLog.Skip(before).ToList();
        var billingQuery = Assert.Single(commands, c => c.Contains("Customers"));
        Assert.Contains("Country", billingQuery); // the filter reached the inner SQL
        Assert.Contains("IN", billingQuery);      // and composed with the semi-join
    }

    [Fact]
    public async Task Where_after_join_touching_both_sides_stays_in_memory()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          where c1.Name == c2.Name
                          select new { c1.Id })
            .ToListAsync();

        Assert.Equal(2, rows.Count); // Bob and Carla match by name in both databases
    }

    [Fact]
    public async Task Projection_of_outer_side_narrows_inner_fetch_to_key_column()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.BillingSqlLog)
            before = fixture.BillingSqlLog.Count;

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join i in billing.Invoices on c1.Id equals i.CustomerId
                          select new { c1.Name })
            .ToListAsync();

        // Bob has two invoices, Carla one — multiplicity must survive the narrowing.
        Assert.Equal(["Bob", "Bob", "Carla"], rows.Select(r => r.Name).Order().ToArray());

        List<string> commands;
        lock (fixture.BillingSqlLog)
            commands = fixture.BillingSqlLog.Skip(before).ToList();
        var invoicesQuery = Assert.Single(commands, c => c.Contains("Invoices"));
        Assert.DoesNotContain("Amount", invoicesQuery); // only the key column was fetched
    }

    [Fact]
    public async Task Projection_of_inner_side_narrows_outer_fetch_to_key_column()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.CrmSqlLog)
            before = fixture.CrmSqlLog.Count;

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          select new { c2.Country })
            .ToListAsync();

        Assert.Equal(["DE", "IT"], rows.Select(r => r.Country).Order().ToArray());

        List<string> commands;
        lock (fixture.CrmSqlLog)
            commands = fixture.CrmSqlLog.Skip(before).ToList();
        var crmQuery = Assert.Single(commands, c => c.Contains("Customers"));
        Assert.DoesNotContain("Country", crmQuery); // outer side fetched only its key
        Assert.DoesNotContain("Name", crmQuery);
    }

    [Fact]
    public async Task Pushed_where_and_narrowed_projection_compose()
    {
        await using var crm = fixture.CreateCrm();
        await using var billing = fixture.CreateBilling();

        int before;
        lock (fixture.BillingSqlLog)
            before = fixture.BillingSqlLog.Count;

        var rows = await (from c1 in crm.Customers.AsCrossQuery()
                          join c2 in billing.Customers on c1.Id equals c2.Id
                          where c1.Country == "IT"
                          select c1)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Carla", row.Name);

        List<string> commands;
        lock (fixture.BillingSqlLog)
            commands = fixture.BillingSqlLog.Skip(before).ToList();
        var billingQuery = Assert.Single(commands, c => c.Contains("Customers"));
        Assert.DoesNotContain("Name", billingQuery); // inner narrowed to key even with entity projection
    }

    [Fact]
    public async Task Semi_join_batches_large_key_sets()
    {
        var previous = CrossEfOptions.MaxSemiJoinKeysPerQuery;
        CrossEfOptions.MaxSemiJoinKeysPerQuery = 2;
        try
        {
            await using var crm = fixture.CreateCrm();
            await using var billing = fixture.CreateBilling();

            int before;
            lock (fixture.BillingSqlLog)
                before = fixture.BillingSqlLog.Count;

            var rows = await (from c1 in crm.Customers.AsCrossQuery()
                              join c2 in billing.Customers on c1.Id equals c2.Id
                              select new { c1.Id })
                .ToListAsync();

            Assert.Equal(2, rows.Count); // results identical despite batching

            List<string> commands;
            lock (fixture.BillingSqlLog)
                commands = fixture.BillingSqlLog.Skip(before).ToList();
            // 3 outer keys with batch size 2 -> two queries against the inner database
            // (EF renders the single-key second batch as '=' rather than 'IN').
            Assert.Equal(2, commands.Count(c => c.Contains("Customers")));
        }
        finally
        {
            CrossEfOptions.MaxSemiJoinKeysPerQuery = previous;
        }
    }

    [Fact]
    public void Synchronous_enumeration_works()
    {
        using var crm = fixture.CreateCrm();
        using var billing = fixture.CreateBilling();

        var rows = (from c1 in crm.Customers.AsCrossQuery()
                    join c2 in billing.Customers on c1.Id equals c2.Id
                    select new { c1.Id })
            .ToList();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ToCrossList_synchronous_without_marker()
    {
        using var crm = fixture.CreateCrm();
        using var billing = fixture.CreateBilling();

        var rows = (from c1 in crm.Customers
                    join c2 in billing.Customers on c1.Id equals c2.Id
                    select new { c1.Id })
            .ToCrossList();

        Assert.Equal(2, rows.Count);
    }
}
