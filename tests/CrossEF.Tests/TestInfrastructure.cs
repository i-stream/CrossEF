using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CrossEF.Tests;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Country { get; set; } = "";
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
}

public class Invoice
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Amount { get; set; }
}

public class CrmContext(DbContextOptions<CrmContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
}

public class BillingContext(DbContextOptions<BillingContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
}

/// <summary>
/// Two completely separate SQLite in-memory databases, one per context type.
/// CRM has customers 1 Alice (IT), 2 Bob (DE), 3 Carla (IT) and two orders.
/// Billing has customers 2 Bob, 3 Carla, 4 Dan and three invoices.
/// </summary>
public sealed class TwoDatabaseFixture : IDisposable
{
    private readonly SqliteConnection _crmConnection = new("Data Source=:memory:");
    private readonly SqliteConnection _billingConnection = new("Data Source=:memory:");

    public List<string> BillingSqlLog { get; } = [];

    public List<string> CrmSqlLog { get; } = [];

    public TwoDatabaseFixture()
    {
        _crmConnection.Open();
        _billingConnection.Open();

        using var crm = CreateCrm();
        crm.Database.EnsureCreated();
        crm.AddRange(
            new Customer { Id = 1, Name = "Alice", Country = "IT" },
            new Customer { Id = 2, Name = "Bob", Country = "DE" },
            new Customer { Id = 3, Name = "Carla", Country = "IT" });
        crm.AddRange(
            new Order { Id = 1, CustomerId = 1, Total = 100m },
            new Order { Id = 2, CustomerId = 2, Total = 250m });
        crm.SaveChanges();

        using var billing = CreateBilling();
        billing.Database.EnsureCreated();
        billing.AddRange(
            new Customer { Id = 2, Name = "Bob", Country = "DE" },
            new Customer { Id = 3, Name = "Carla", Country = "IT" },
            new Customer { Id = 4, Name = "Dan", Country = "US" });
        billing.AddRange(
            new Invoice { Id = 1, CustomerId = 2, Amount = 40m },
            new Invoice { Id = 2, CustomerId = 3, Amount = 60m },
            new Invoice { Id = 3, CustomerId = 4, Amount = 80m },
            new Invoice { Id = 4, CustomerId = 2, Amount = 45m }); // second invoice for Bob: exercises join multiplicity
        billing.SaveChanges();
    }

    public CrmContext CreateCrm()
        => new(new DbContextOptionsBuilder<CrmContext>()
            .UseSqlite(_crmConnection)
            .LogTo(message => { lock (CrmSqlLog) CrmSqlLog.Add(message); },
                [RelationalEventId.CommandExecuted])
            .Options);

    public BillingContext CreateBilling()
        => new(new DbContextOptionsBuilder<BillingContext>()
            .UseSqlite(_billingConnection)
            .LogTo(message => { lock (BillingSqlLog) BillingSqlLog.Add(message); },
                [RelationalEventId.CommandExecuted])
            .Options);

    public void Dispose()
    {
        _crmConnection.Dispose();
        _billingConnection.Dispose();
    }
}
