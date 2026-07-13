# CrossEF

Cross-`DbContext` LINQ queries for Entity Framework Core. Join entities that live in **different DbContexts** — different databases, different servers, even different providers — in a single LINQ query.

EF Core refuses to execute a query that references two context instances:

> *"Cannot use multiple context instances within a single query execution."*

CrossEF works around that limitation by acting as a small **federated query engine**: it splits the expression tree into single-context fragments, executes each fragment on its own context (keeping filters translated to SQL), and joins the results itself in memory.

```csharp
// One LINQ query, two databases:
var rows = await (from c in crmContext.Customers
                  join b in billingContext.Customers on c.Id equals b.Id
                  select new { c.Name, b.Balance })
    .ToCrossListAsync();
```

## Requirements

- .NET 10
- EF Core 10 — any provider (SQL Server, SQLite, Npgsql, in-memory, …); the two sides of a query can even use **different providers**.

## Installation

```
dotnet add package CrossEF
```

Then import the namespace:

```csharp
using CrossEF;
```

## Getting started

You need nothing special in your `DbContext` classes — CrossEF works with plain, unmodified contexts. There are two equivalent ways to run a cross-context query:

### Option A — mark the query with `.AsCrossQuery()`

Call `.AsCrossQuery()` on the **first** source, then compose with the normal LINQ/EF operators and finish with any standard EF operator (`ToListAsync`, `CountAsync`, `FirstAsync`, `foreach`, …):

```csharp
using CrossEF;

var rows = await (from c1 in crmContext.Customers.AsCrossQuery()
                  join c2 in billingContext.Customers on c1.Id equals c2.Id
                  select new { c1.Id, CrmName = c1.Name, BillingName = c2.Name })
    .ToListAsync();
```

### Option B — no marker, finish with `ToCrossListAsync()`

Write a completely ordinary EF query and just end it with `ToCrossListAsync()` (or the synchronous `ToCrossList()`):

```csharp
using CrossEF;

var rows = await (from c1 in crmContext.Customers
                  join c2 in billingContext.Customers on c1.Id equals c2.Id
                  select new { c1, c2 })
    .ToCrossListAsync();
```

Use **Option A** when you want the full set of EF terminal operators (`CountAsync`, `FirstAsync`, `SumAsync`, `await foreach`, …) on the cross query. Use **Option B** when you just want a list and prefer to keep the query body untouched.

## What you can do

### Join across two databases

```csharp
var rows = await (from c in crm.Customers.AsCrossQuery()
                  join i in billing.Invoices on c.Id equals i.CustomerId
                  select new { c.Name, i.Amount })
    .ToListAsync();
```

CrossEF executes the CRM side first, collects the join keys, and fetches the Billing side with a `WHERE CustomerId IN (…)` **semi-join** — it never scans the whole inner table.

### Filter each side in SQL

Filters written **before** the cross-context boundary (before `.AsCrossQuery()` or inside a join source) always run as SQL on their own context:

```csharp
var rows = await (from c in crm.Customers.Where(c => c.Country == "IT").AsCrossQuery()
                  join i in billing.Invoices.Where(i => i.Amount > 50) on c.Id equals i.CustomerId
                  select new { c.Name, i.Amount })
    .ToListAsync();
```

### Filter after the join — pushed down automatically when possible

A `where` written **after** the join that references only **one side** is automatically pushed down into that side's SQL:

```csharp
var rows = await (from c1 in crm.Customers.AsCrossQuery()
                  join c2 in billing.Customers on c1.Id equals c2.Id
                  where c1.Country == "IT"       // pushed down: runs as SQL on the CRM database
                  select new { c1.Name })
    .ToListAsync();
```

A `where` that touches **both** sides cannot run on either database, so it executes in memory over the fetched rows:

```csharp
where c1.Name == c2.Name   // runs in memory (correct, just not in SQL)
```

### Project one side — the other side ships only its key

When the final projection reads only **one side** of the join, the other side is narrowed to its join-key column in SQL — full rows are never fetched (join multiplicity is preserved, duplicates included):

```csharp
var rows = await (from c in crm.Customers.AsCrossQuery()
                  join i in billing.Invoices on c.Id equals i.CustomerId
                  select new { c.Name })   // Billing only ships Invoices.CustomerId values
    .ToListAsync();
```

### Left joins (`GroupJoin` / `DefaultIfEmpty`)

Left joins work, but fall back to fully materializing each side (no semi-join narrowing):

```csharp
var rows = await (from c1 in crm.Customers.AsCrossQuery()
                  join c2 in billing.Customers on c1.Id equals c2.Id into matches
                  from c2 in matches.DefaultIfEmpty()
                  select new { c1.Name, BillingName = c2 != null ? c2.Name : null })
    .ToListAsync();
```

### Scalar operators

On a marked query, EF's async scalar operators work as usual:

```csharp
var count = await (from c1 in crm.Customers.AsCrossQuery()
                   join c2 in billing.Customers on c1.Id equals c2.Id
                   select c1.Id)
    .CountAsync();
```

### Synchronous and streaming enumeration

```csharp
// Synchronous
var list = query.ToList();          // on a marked query
var list = query.ToCrossList();     // on an unmarked query

// Async streaming (results are buffered internally, then yielded)
await foreach (var row in query)   { ... }
```

### Same-context queries pass through untouched

If a query (marked or not) only references **one** context, CrossEF hands the whole expression tree to EF unchanged — one SQL statement, no in-memory work. You can therefore sprinkle `.AsCrossQuery()` defensively without a penalty on single-context queries.

## How it works

1. **Normalize** — closure references such as `ctx.Customers` are resolved; CrossEF wrappers are unwrapped so nested cross queries compose.
2. **Analyze** — every EF query root in the tree is grouped by its owning `DbContext` (query provider).
3. **Plan & execute**
   - **Zero contexts** → pure in-memory query; compiled and run directly.
   - **One context** → the whole query is handed to EF unchanged: one SQL statement, zero overhead besides planning.
   - **Multiple contexts** → the tree is split into maximal single-context fragments. Each fragment runs on its own context, so `Where`/`Select` written *inside* a fragment still translate to SQL.
     - For the common `join` pattern, CrossEF executes the outer side first, collects its join keys, and fetches the inner side with a **semi-join** (`WHERE key IN (…)`) instead of scanning the whole table.
     - Everything above the fragments (the join itself, later filters, projections, ordering) runs in memory via LINQ to Objects.

## Performance guidance

- **Put per-context filters before the cross-context boundary** (before `.AsCrossQuery()` or inside the join source) so they translate to SQL. Single-side `where` clauses after the join are pushed down for you, and a projection that reads only one side narrows the other side to its key column; anything else after the boundary runs in memory.
- **Order the join so the smaller / more-filtered side is the outer one.** The outer side is fetched first and its keys drive the semi-join against the inner side.
- The semi-join optimization applies to `join ... on ... equals ...` with a **simple key** (numeric, string, `Guid`, dates, enums). Composite (anonymous-type) keys and `GroupJoin`/left joins fall back to fully materializing each side.
- Large key sets are sent in batches of `CrossEfOptions.MaxSemiJoinKeysPerQuery` (default **2000**) to stay below provider limits such as SQL Server's expression services limit:

  ```csharp
  CrossEfOptions.MaxSemiJoinKeysPerQuery = 500;   // global setting
  ```

- Connection setup cost (e.g. a TLS handshake per server) is paid once per context. If startup latency matters, warm the connections up front and in parallel:

  ```csharp
  await Task.WhenAll(
      contextA.Database.OpenConnectionAsync(),
      contextB.Database.OpenConnectionAsync());
  ```

## Limitations

- The cross-context join itself (and any operator above it that isn't a pushable single-side `where`) executes **in memory**, so the amount of data fetched from each side matters — filter early.
- Semi-join narrowing requires a simple join key; composite keys and left joins fetch both sides fully.
- `IAsyncEnumerable` results are buffered before being yielded (no end-to-end streaming yet).
- Change tracking behaves as usual per context — entities are tracked by whichever context loaded them.

## Project layout

- `src/CrossEF` — the library: `CrossQueryable<T>`, `CrossQueryProvider` (an `IAsyncQueryProvider`), and `CrossQueryExecutor` (normalization, fragment planning, semi-join, in-memory stitching).
- `tests/CrossEF.Tests` — xunit suite running two independent SQLite in-memory databases, including SQL-log assertions that the semi-join and pushdowns really happen.

## Roadmap

- [x] Key batching for large semi-join key sets (`CrossEfOptions.MaxSemiJoinKeysPerQuery`)
- [x] Pushdown of a post-join `where` that references only one side of the join
- [x] Post-join `Select` referencing only one side: the unused side is narrowed to its key column
- [ ] Column pruning when the projection reads a subset of both sides
- [ ] Semi-join support for composite keys (anonymous-type keys) and `GroupJoin` (left joins)
- [ ] Choose the smaller side as the key source (currently always outer-first)
- [ ] Same-server fast path for SQL Server (`[OtherDb].dbo.Table` rewrite → one SQL statement)
- [ ] Streaming execution (`IAsyncEnumerable` end-to-end instead of buffering)
- [ ] EF Core 11 preview multi-targeting
