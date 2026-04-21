# PLEXXER Client — AiBuilder

Generated C# client for app `3ovxi6nl1wgdoggvdic64yutd79pdvu8`. Targets .NET 8.

## Quick start

```csharp
// Default host — matches https://api.plexxer.com (the public docs' host).
var client = new PlexxerClient("plx_...");

// Self-hosted / staging — pass a baseUrl explicitly.
// var client = new PlexxerClient("https://your-plexxer-host", "plx_...");

// Create — typed POCO
var created = await client.CreateAsync(new Admin { /* populate fields */ });

// Read — filters as anon object or Dictionary<string, object?>
var all = await client.ReadAsync<Admin>();

// Read with DSL operators — colon suffix keys need a dictionary
var page = await client.ReadAsync<Admin>(new Dictionary<string, object?>
{
    ["query"] = new { limit = 25, offset = 0 },
});

// Update / delete
await client.UpdateAsync<Admin>(
    new Dictionary<string, object?> { ["_id:eq"] = created.Id },
    new Dictionary<string, object?> { [":set"] = new { /* fields to set */ } });
await client.DeleteAsync<Admin>(new { _id = created.Id });
```

## Relations

Relation fields use `RelationRef<T>` (one) or `List<RelationRef<T>>` (many). A ref carries
either an `Id` (hex `_id` — connect to an existing row) or a `Document` (a full POCO —
creates a new row inline).

```csharp
// Connect an existing row
new Order { Customer = "64b8..." }          // implicit: string → RelationRef<Customer>
new Order { Customer = new RelationRef<Customer>("64b8...") }  // explicit

// Create the related row inline (single round-trip nested write)
new Order { Customer = new Customer { Name = "Alice" } }       // implicit: T → RelationRef<T>

// Many-cardinality — each slot is its own ref (mix connect + create freely)
new Customer { Orders = new() { new Order { Total = 1 }, "64b8..." } };
```

### Eager-load (read-side)

Pass `query.related` to inline related documents on the response. The `RelationRef<T>.Document`
property is populated when eager-loaded; `Id` stays set otherwise.

```csharp
var orders = await client.ReadAsync<Order>(new Dictionary<string, object?>
{
    ["query"] = new { related = new[] { "customer" } },
});
foreach (var o in orders)
    Console.WriteLine(o.Customer?.Document?.Name ?? o.Customer?.Id);
```

### Nested create with full graph back

`CreateWithGraphAsync` sends `?return=graph` — the response includes every inserted child
with its server-assigned `_id` exposed via `RelationRef<T>.Document.Id`.

```csharp
var root = await client.CreateWithGraphAsync(new Customer
{
    Name = "Alice",
    Address = new Address { Street = "1 Main" }, // inlined create
});
var addrId = root.Address?.Document?.Id;
```

## Aggregate & count

Four helpers wrap `POST /d/{appKey}/{entity}/aggregate` — the same DSL as the public
API docs' "Aggregate & count" section. Metric/group results come back untyped (raw dictionary-
or-POCO target); a typed metric builder lands in a later release.

```csharp
// Count matching documents
var howMany = await client.CountAsync<Admin>(new { status = "active" });

// Single-row metrics
public sealed class Stats
{
    public long   N         { get; set; }
    public double TotalRev  { get; set; }
}
var stats = await client.AggregateAsync<Admin, Stats>(new
{
    metrics = new Dictionary<string, object?>
    {
        ["N"] = "count",
        ["TotalRev"] = new { sum = "revenue" },
    },
});

// Group by one key
public sealed class CountryRow
{
    public string? Country { get; set; }
    public long    N       { get; set; }
}
var rows = await client.AggregateGroupAsync<Admin, CountryRow>(new Dictionary<string, object?>
{
    ["groupBy"] = new[] { "country" },
    ["metrics"] = new Dictionary<string, object?> { ["N"] = "count" },
    ["sort"]    = new Dictionary<string, object?> { ["N"] = -1 },
    ["limit"]   = 10,
});

// Distinct scalar values
var countries = await client.DistinctAsync<Admin, string>("country");
```

## Error handling

Non-2xx responses throw `PlexxerException`. The envelope is parsed into `PlexxerException.Error`
when the body is structured JSON.

```csharp
try { await client.CreateAsync(new Member { Email = "not-an-email" }); }
catch (PlexxerException ex) when (ex.ErrorCode == "validation-failed")
{
    foreach (var e in ex.Error!.Errors ?? new()) Console.WriteLine($"{e.Path ?? e.Field}: {e.Code}");
}
```

Common `ex.ErrorCode` values: `validation-failed`, `unknown-fields`, `required-fields-missing`,
`required-relations-missing`, `relation-target-missing`, `unique-violated`, `permission-denied`,
`app-suspended`, `dsl` (with `ex.Error.DslCode` for the sub-code).

## Notes

- Primitive typing mirrors the schema's `required` flag: required primitives are non-nullable
  (`string`, `double`, `bool`, `DateTime`); optional ones are nullable (`string?` etc.).
- `number` maps to `double` — covers integer and floating-point storage alike.
- `date` fields map to `DateTime`/`DateTime?` and round-trip as UTC ISO-8601 on the wire.
- Auto-timestamp fields (`auto: created` / `auto: updated`) are server-managed — values you
  set on the POCO are silently ignored.
- Filters with DSL operators (`:eq`, `:like`, etc.) need a `Dictionary<string, object?>` —
  anonymous-object property names can't contain colons.

## Entities in this package

- `Admin`
- `BuildRun`
- `ConversationTurn`
- `DeployRun`
- `Project`
- `TargetEnvVar`
