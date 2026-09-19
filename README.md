# resume_csharp — the link backend, in C# (ASP.NET Core)

A fourth implementation of the short-link API. The Next.js app in
[`resume_nextjs`](https://github.com/kmonagle/resume_nextjs) can serve links itself out of
its own code, and the [Go](https://github.com/kmonagle/resume_go) and
[Python](https://github.com/kmonagle/resume_python) services do the same job; this one does
it in C#. All of them are held to the **identical contract test suite**, which is the point:
the contract, not the language, defines the system.

**Implements contract `contract-v1`** (the tag pinned in `.github/workflows/ci.yml`).

Read this README for how the services fit together and why the design is the way it is. Read
the code for the C#: every file opens with a comment on why it exists, and comments tagged
**`JS/TS vs C#:`** call out where C# behaves differently from what a JavaScript/TypeScript
developer would expect (`grep -rn "JS/TS vs C#" src tests`).

> The integration story (browser → Next.js → backend → Postgres, the token, the redirect flow,
> cold starts, the shared database) is identical for every backend. The
> [Go README](https://github.com/kmonagle/resume_go#readme) has the long version; the
> essentials are repeated below so this one stands alone.

---

## The big picture

```
                    cookie: visitor_id                Authorization: Bearer <token>
                    (httpOnly, Next's domain)         X-Owner-Id: <visitor id>
┌─────────┐  HTTPS  ┌──────────────────────┐  HTTPS   ┌──────────────┐  SQL   ┌──────────┐
│ Browser │ ──────► │ Next.js (UI + BFF)   │ ───────► │ this C#      │ ─────► │  Neon    │
│         │ ◄────── │ resume_nextjs        │ ◄─────── │ service      │ ◄───── │ Postgres │
└─────────┘         └──────────────────────┘          └──────────────┘        └──────────┘
```

- The **browser never talks to this service**. It only sees the Next.js domain, so there is no
  CORS or cross-site-cookie problem, and the bearer token and this service's URL never reach
  client JavaScript. Next.js is a **BFF** (backend-for-frontend).
- Next.js picks its backend with an environment variable, `LINK_BACKEND`: **`local`** (its own
  Drizzle code) or **`remote`** (call one of these services, at `LINK_BACKEND_URL`). Point it at
  Go, Python or C# and the UI can't tell.
- **All backends share one Postgres database**, so links carry across them.

### Who owns what

| Concern | Owner |
|---|---|
| UI, dashboard polling, forms, the visitor cookie | Next.js |
| Input validation | **Both**: Next validates first (fast form errors); this service validates again because it must not trust its caller. Rules and messages match. |
| Business rules (limits, retention, 404 vs 410), atomic click counting | **Every backend**, with the same behaviour |
| Click event logging | Whoever serves the redirect (here). Next.js must not log too or clicks double count. |
| **Database schema and migrations** | **The Next.js repo.** This service never migrates. |
| The contract (OpenAPI spec + tests) | The Next.js repo, pinned by tag |

### How each Next.js feature becomes calls to this service

| In the UI | Next.js calls | Auth |
|---|---|---|
| Create a link | `POST /links` | bearer + `X-Owner-Id` |
| Dashboard (polls every 5 s) | `GET /links` | bearer + `X-Owner-Id` |
| Activate / Deactivate | `PATCH /links/{id}` | bearer + `X-Owner-Id` |
| Follow a short link `/r/{code}` | `GET /r/{code}` (redirect **not** followed) | none (public) |
| Footer "Served by: …" | `GET /meta` | none (public) |

### Identity and trust

Next.js gives each browser a random `visitor_id` cookie and copies it into an `X-Owner-Id`
header on every call. This service trusts that header **only because the request also carries
the shared bearer token** (`LINK_BACKEND_TOKEN`). Render's free tier has no private networking,
so the service is reachable from the internet and the token is the only thing stopping anyone
from claiming to be any owner. Every query is also scoped by owner (`WHERE id = @id AND
owner_id = @owner`), so someone else's link id returns `404` (not `403`, which would confirm it
exists). The token is compared as SHA-256 hashes with `CryptographicOperations.FixedTimeEquals`,
so timing can't be used to guess it. (See `TokenVerifier` and `RequireOwnerFilter` in
`src/LinkApi/Endpoints/RequireOwnerFilter.cs`.)

The cookie identifies a *browser*, not a person: it is scoping for a login-free demo, not
authentication.

### The redirect

```
browser ─ GET /r/abc ─► Next.js ─ GET /r/abc ─► this service
                          (redirect: "manual",   │ 1. one atomic UPDATE: check every rule
                           forwards Referer +    │    AND count the click
                           User-Agent)           │ 2. reply 307 + Location
                                                 │ 3. OnCompleted callback logs the click_events row
browser ◄─ 307 Location ─ Next.js ◄─ 307 ───────┘
```

Two things specific to this implementation:

- **The click is logged after the response is sent**, using `Response.OnCompleted`, so analytics
  never slows the redirect. The callback can't reuse the request's services (the scope, and the
  database context in it, is finished by then), so it opens its own scope. A durable version
  would queue the event to a `BackgroundService`; losing a click log is acceptable here, so a
  failure is logged and dropped. (Verified: the row is written with the visitor's forwarded
  `Referer` and `User-Agent`.)
- **Click limits are enforced by one SQL statement** (`EfLinkStore.ClaimAsync`): check and
  increment together. Loading the link, checking in C#, then saving would let two simultaneous
  requests both read "9 of 10", both pass, and both redirect. In a single `UPDATE`, Postgres
  locks the row, so the second request waits and then fails the `WHERE` clause. The contract
  suite fires 12 requests in parallel at a limit-2 link and expects exactly 2 redirects.
  *EF Core can't express `UPDATE ... RETURNING`*, so the claim is decided by the statement's
  **rows-affected count** (1 = claimed, 0 = not redeemable), and the target URL is then read
  with a plain `SELECT`. That is safe: the decision was already made atomically, and a link's
  target never changes.

## What Next.js does when this service misbehaves

Next.js validates every response with zod and maps outcomes: `201` → created; `409` → "code
taken" (a form field error); `429` → "limit reached" (this service's message is shown); `404` on
toggle → not found; `404`/`410` on a redirect → passed through; `401` → a misconfigured token
(logged, shown as "backend unavailable"); `5xx`, invalid JSON or a timeout → "backend
unavailable". That becomes a `503` from the JSON API, a "waking up, try again" message on the
form, a `503` + `Retry-After` on a short link, and "live updates paused" on the dashboard.

## Free-tier cold starts (Render)

A free service sleeps after 15 idle minutes and takes about a minute to wake. With Next.js in
front, both can be asleep, so Next.js pings this service's `/meta` when it starts
(fire-and-forget) so the two wake in parallel, and uses a 90-second timeout. A free workspace
gets about 750 instance-hours a month: one always-on service uses ~730, so don't try to keep two
awake. `LINK_BACKEND=local` needs no second service at all.

## The shared database

- **Schema ownership.** Tables are defined by Drizzle in the Next.js repo
  (`src/server/db/schema.ts`; generated SQL in `drizzle/`). The Next.js service applies
  migrations on every deploy; **this service never migrates**, so there are **no EF Core
  migrations** here, and `Data/LinksDbContext.cs` is a hand-kept mirror of the tables. On a new
  database, deploy or migrate Next.js first, or you'll see `relation "links" does not exist`.
- **Small differences from the ORM.** Drizzle generates ids and sets `updated_at` in application
  code; here ids are `Guid`s made in C#, and every update sets `updated_at` to the database's
  `now()` by hand. `owner_id` is `NOT NULL` with no default, so every insert must supply one.
- **Neon and Npgsql.** The URL Neon hands out (`postgres://…?sslmode=require&channel_binding=
  require`) is not a format Npgsql accepts, and `Configuration/DatabaseUrl.cs` converts it: the
  URL becomes a key=value connection string, `sslmode` becomes `SslMode`, and `channel_binding` is
  ignored. Because Neon's pooled URL goes through PgBouncer in transaction mode, the pool
  doesn't reset connections on return (`No Reset On Close`), and it is capped at 5 connections
  because several services share one database. `tests/LinkApi.Tests/DatabaseUrlTests.cs` pins
  this down.
- **Changing the schema safely.** Because several backends share the database, a migration must
  leave *older* backends working (add a nullable column, upgrade every backend, then tighten it).

## The contract

`docs/openapi.yaml` in the Next.js repo is the source of truth. `.github/workflows/ci.yml` here
pins the version this service implements (`CONTRACT_REF: contract-v1`, a git tag); CI checks it
out, applies its `drizzle/*.sql` to a throwaway Postgres, starts this service, and runs the shared
suite against it. To upgrade, bump the tag, make the new tests pass, and merge; other backends can
stay on the old tag meanwhile, so prefer *additive* contract changes.

### Four implementations, side by side

| C# (this repo) | Python (`resume_python`) | Go (`resume_go`) | Next.js (`resume_nextjs`) | Job |
|---|---|---|---|---|
| `Data/EfLinkStore.cs` | `app/store.py` | `internal/store` | `link-repository.ts` | the only code that runs queries |
| `Data/LinksDbContext.cs` | `app/models.py` | (plain SQL) | `schema.ts` | the table definitions |
| `Services/LinkService.cs` | `app/service.py` | `internal/service` | `link-api/local.ts` | limits, retention, codes, 404 vs 410 |
| `Endpoints/` | `app/api.py` | `internal/api` | `src/app/api/**`, `src/app/r/**` | HTTP handlers, auth |
| `Contracts/` | `app/schemas.py` | `internal/link/validate.go` | `link-schema.ts` | input validation, wire format |
| `Domain/` | `app/domain.py` | `internal/link/link.go` | `link-status.ts` | "is this link usable?" |
| `Configuration/` | `app/config.py`, `app/db.py` | `internal/config` | `env.ts`, `db/client.ts` | environment, connecting to Postgres |

The trade-offs show up in the numbers: this image is ~380 MB (it carries the .NET runtime),
against ~270 MB for Python and ~20 MB for Go's single static binary.

## Environment variables

| Variable | Where | Meaning |
|---|---|---|
| `DATABASE_URL` | this service | Postgres URL. Use Neon's **pooled** URL in production. |
| `LINK_BACKEND_TOKEN` | this service **and** Next.js | Shared secret, 16+ characters. **Must be identical on both.** |
| `PORT` | this service | Render sets it; defaults to `8080`. |
| `LINK_BACKEND=remote`, `LINK_BACKEND_URL` | Next.js | Point Next.js at this service's public URL. |

## Run it locally

```bash
# 1. Postgres, with the schema applied by the Next.js repo
docker run -d --name links-pg -e POSTGRES_PASSWORD=dev -e POSTGRES_DB=links -p 54329:5432 postgres:17
(cd ../resume_nextjs && DIRECT_URL=postgres://postgres:dev@localhost:54329/links npx drizzle-kit migrate)

# 2. This service, in Docker (no .NET install needed)
docker build -t resume-csharp .
docker run --rm -p 8080:8080 \
  -e DATABASE_URL="postgres://postgres:dev@host.docker.internal:54329/links" \
  -e LINK_BACKEND_TOKEN="local-dev-token-0123456789" resume-csharp
#    ...or with the .NET 10 SDK installed:
#    DATABASE_URL=... LINK_BACKEND_TOKEN=... dotnet run --project src/LinkApi

# 3. Next.js in front of it (from ../resume_nextjs)
LINK_BACKEND=remote LINK_BACKEND_URL=http://localhost:8080 \
LINK_BACKEND_TOKEN=local-dev-token-0123456789 npm run dev
```

**Tests**

```bash
dotnet test                                         # unit tests: no database needed
dotnet format --verify-no-changes                   # formatting/style (also run in CI)
docker build --target test -t resume-csharp-test .  # the same tests, inside Docker

# the shared contract suite against this service directly (from ../resume_nextjs):
CONTRACT_BASE_URL=http://localhost:8080 CONTRACT_IDENTITY=header \
CONTRACT_API_PREFIX="" CONTRACT_TOKEN=local-dev-token-0123456789 npm run test:contract
```

## Deploy on Render

Create a **Web Service** from this repo, runtime **Docker**, and set `DATABASE_URL` (the Neon
**pooled** URL) and `LINK_BACKEND_TOKEN`. Render provides `PORT`. Set the health check path to
`/meta`. To use it, set `LINK_BACKEND=remote`, `LINK_BACKEND_URL` and the same
`LINK_BACKEND_TOKEN` on the Next.js service. Setting **Auto-Deploy** to "After CI Checks Pass"
keeps a broken push out of production.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Next.js logs `Backend … failed (401)` | The two `LINK_BACKEND_TOKEN` values differ. |
| First page load takes a minute or two | Cold start: both services were asleep. |
| Startup fails with "Invalid configuration" | `DATABASE_URL` is missing, or `LINK_BACKEND_TOKEN` is under 16 characters. |
| `relation "links" does not exist` | The schema hasn't been applied; migrate the Next.js repo first. |
| Clicks never appear in `click_events` | The `OnCompleted` callback is failing (see the logs under `LinkApi.Clicks`). |
| Clicks counted twice | Something else is also logging click events. |
| CI can't check out the contract | The tag isn't pushed, the Next.js repo is private, or `CONTRACT_REPO` is wrong. |
| Want to see the SQL EF sends | Set `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information`. |

## Layout

```
src/LinkApi/Program.cs                 builds the app: configuration, DI container, pipeline
src/LinkApi/Endpoints/                 minimal-API routes; the auth filter (bearer token + owner)
src/LinkApi/Services/LinkService.cs    business rules; the ILinkStore interface; result types
src/LinkApi/Data/EfLinkStore.cs        the ONLY class that runs queries (EF Core)
src/LinkApi/Data/LinksDbContext.cs     table classes: a MIRROR of the schema Drizzle owns
src/LinkApi/Contracts/                 request validation, wire format, contract-shaped errors
src/LinkApi/Domain/                    Link, status rules, short-code generation (no I/O)
src/LinkApi/Configuration/             typed settings; converts Neon's URL for Npgsql
tests/LinkApi.Tests/                   xUnit: domain, validation, config, service, API (no database)
Dockerfile                             four stages: restore, test, publish, runtime
.editorconfig                          style rules enforced by `dotnet format`
```

Suggested reading order: `Domain/` → `Contracts/` → `Data/LinksDbContext.cs` →
`Data/EfLinkStore.cs` → `Services/LinkService.cs` → `Endpoints/` → `Program.cs`.

## Design decisions worth talking about

Each of these is a choice with a reason, and the trade-off is stated so it can be challenged.

- **A conventional stack, on purpose.** ASP.NET Core minimal APIs, EF Core over Npgsql, the
  built-in dependency-injection container, the options pattern, xUnit and `dotnet format`: the
  mainstream .NET service stack, so a .NET developer can navigate it without learning anything
  bespoke.
- **Minimal APIs, not controllers.** Routes are plain functions registered in one file, in the
  style of Express, which keeps the endpoint layer short enough to read at a glance. Controllers
  earn their keep on large APIs with lots of attributes and conventions; this has five routes.
- **EF Core for everything except the one statement that matters.** Reads, inserts and toggles use
  LINQ and the change tracker. The click claim is a single `UPDATE ... WHERE` (via
  `ExecuteUpdate`) because the load-modify-save sequence would let concurrent requests overshoot a
  click limit. The ORM is a convenience; correctness lives in the database, and the code says so
  where it counts.
- **Catching the unique violation instead of `ON CONFLICT`.** The other implementations insert
  with `ON CONFLICT DO NOTHING`. EF Core has no such construct, and the idiomatic equivalent is to
  let the database arbitrate and catch SQLSTATE `23505` (with an exception filter). Either way it
  is the *database* that decides a race between two requests for the same code; a
  check-then-insert would leave a gap.
- **Auth as an endpoint filter on a route group.** Every route under `/links` needs the token; a
  route that isn't in the group is public by construction. Filters run before the body is read, so
  an unauthenticated caller never learns whether its body was valid.
- **`ILinkStore` is an interface the service depends on,** so tests pass a small fake, with no
  mocking library. (.NET interfaces are nominal: the store says `: ILinkStore`, unlike Go's and
  Python's structural ones.)
- **Expected outcomes are values.** `Created | CodeTaken | LimitReached` is a closed hierarchy of
  records matched with a `switch` expression, and only genuine failures are exceptions.
- **`TimeProvider` for the clock.** The service takes .NET's built-in clock abstraction, so tests
  freeze time instead of racing it.
- **Configuration is validated at startup.** The options pattern with `ValidateOnStart` stops the
  process with a clear message on a bad setting, and building the app never touches the database,
  which is what lets tests start the real pipeline without one.
- **Nullable reference types on, and their warnings are errors.** `string` can't be null and
  `string?` can, enforced by the compiler; it is the first thing to turn on in a new project.
- **Source-generated regex** (`[GeneratedRegex]`): the matching code is written at compile time, so
  there is no startup cost and no runtime pattern parsing.
- **Formatting is checked, not argued about.** `.editorconfig` plus `dotnet format
  --verify-no-changes` in CI.
- **Two things found by looking at the SQL.** Turning on EF's command logging showed that
  `SetProperty(l => l.UpdatedAt, DateTimeOffset.UtcNow)` sent the app's clock as a parameter,
  while `SetProperty(l => l.UpdatedAt, l => DateTimeOffset.UtcNow)` becomes the database's `now()`
  (as in the other implementations). It also confirmed the claim's expiry check runs on the
  database clock.
- **The contract is pinned and tested,** so "same behaviour in four languages" is checked on every
  push, not just claimed.
