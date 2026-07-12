# CrossEF

Cross-`DbContext` LINQ queries for Entity Framework Core. Join entities that live in **different DbContexts** — different databases, different servers, even different providers — in a single LINQ query.

EF Core refuses to execute a query that references two context instances. CrossEF works around that fundamental limitation by acting as a small **federated query engine**: it splits the expression tree into single-context fragments, executes each fragment on its own context (keeping filters translated to SQL), and joins the results itself.

## Usage

Two equivalent entry points:

```csharp
// A) Mark the source with .AsCrossQuery(), then use the normal EF operators.
var rows = await (from c1 in crmContext.Customers.AsCrossQuery()
                  join c2 in billingContext.Customers on c1.Id equals c2.Id
                  select new { c1, c2 })
    .ToListAsync();

// B) No marker at all — just finish with ToCrossListAsync().
var rows = await (from c1 in crmContext.Customers
                  join c2 in billingContext.Customers on c1.Id equals c2.Id
                  select new { c1, c2 })
    .ToCrossListAsync();
```

Also available: `ToCrossList()` (sync), synchronous enumeration (`foreach` / `.ToList()`), `await foreach`, and the EF async scalar operators (`CountAsync`, `FirstAsync`, ...) on marked queries.

## How it works

1. **Normalize** — closure references such as `ctx.Customers` are resolved; CrossEF wrappers are unwrapped.
2. **Analyze** — every EF query root in the tree is grouped by its owning `DbContext` (query provider).
3. **Plan & execute**
   - **One context** → the whole query is handed to EF unchanged: one SQL statement, zero overhead besides planning.
   - **Multiple contexts** → the tree is split into maximal single-context fragments. Each fragment runs on its own context, so `Where`/`Select` written *inside* a fragment still translate to SQL.
     - For the common `join` pattern, CrossEF executes the outer side first, collects its join keys, and fetches the inner side with a **semi-join** (`WHERE key IN (…)`) instead of scanning the whole table.
     - Everything above the fragments (the join itself, later filters, projections, ordering) runs in memory via LINQ to Objects.

### Performance guidance

- A `where` written **after** the join that references only one side is automatically pushed down
  into that side's SQL:
  ```csharp
  from n in ctx1.Orders
  join e in ctx2.Customers on n.CustomerId equals e.Id
  where n.OrderId == 90336        // runs as SQL on ctx1
  select new { n, e }
  ```
- Other operators (projections, ordering, predicates touching both sides) written after the
  cross-context boundary execute in memory over the fetched rows — put per-context filters
  before `.AsCrossQuery()` (or inside the join source) when in doubt.
- The semi-join optimization applies to `join ... on ... equals ...` with a simple key (numeric,
  string, `Guid`, dates, enums). Large key sets are fetched in batches of
  `CrossEfOptions.MaxSemiJoinKeysPerQuery` (default 2000) to stay below provider limits such as
  SQL Server's expression services limit. Other shapes (e.g. `GroupJoin`/left join, composite
  keys) currently fall back to fully materializing each side.

## Requirements

- .NET 10, EF Core 10 (any relational or non-relational provider; sides of a query can use different providers).

## Project layout

- `src/CrossEF` — the library: `CrossQueryable<T>`, `CrossQueryProvider` (an `IAsyncQueryProvider`), and `CrossQueryExecutor` (normalization, fragment planning, semi-join, in-memory stitching).
- `tests/CrossEF.Tests` — xunit suite running two independent SQLite in-memory databases.

## Roadmap

- [x] Key batching for large semi-join key sets (`CrossEfOptions.MaxSemiJoinKeysPerQuery`)
- [x] Pushdown of a post-join `where` that references only one side of the join
- [ ] Pushdown of post-join `Select` projections referencing only one side
- [ ] Semi-join support for composite keys (anonymous-type keys) and `GroupJoin` (left joins)
- [ ] Choose the smaller side as the key source (currently always outer-first)
- [ ] Same-server fast path for SQL Server (`[OtherDb].dbo.Table` rewrite → one SQL statement)
- [ ] Streaming execution (`IAsyncEnumerable` end-to-end instead of buffering)
- [ ] EF Core 11 preview multi-targeting
