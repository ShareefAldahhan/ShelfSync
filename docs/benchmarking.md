# API capacity benchmarking

Measured local results and the connection-pool fix are recorded in [the October 25 report](benchmark-results-2026-10-25.md).

Run from the repository root with Node.js 24, the .NET 10 SDK (restored dependencies), and PostgreSQL 18 tools installed:

```powershell
node scripts/Benchmark-Api.mjs
```

The script publishes a Release API and creates a new PostgreSQL cluster under `artifacts/benchmark-<timestamp>`. It uses loopback ports **5091** and **55440**, refuses occupied ports, disables Stripe, generates temporary credentials, and stops its own processes afterward. It never reads development credentials or connects to `shelfsync_dev`. Logs, raw JSON measurements, and a Markdown report remain in that directory. The temporary password file is removed after database initialization. The retained cluster contains fictional data only.

If Windows sandbox restrictions prevent PostgreSQL from launching, run from a normal local terminal. Do not point a load generator at the store's development database or at Stripe.

## Workloads

- **Browse:** 60% full catalog, 30% individual product reads, 10% paginated staff order-list reads.
- **Reserve/cancel:** create a one-unit reservation, then cancel it, distributed across 500 products.
- **Hot product:** the same reservation/cancellation workflow against one product, exercising inventory row-lock contention.

Each run seeds 500 products with 10,000 units each and 5,000 cancelled historical orders. Reservation workflows add more orders and audit records as the test progresses. After the load, zero active reservations and unchanged stock are required. Each write workflow performs **two API requests**, so requests/second is not orders/second.

Authentication and authorization, CSRF validation, transactions, database locks, and idempotency checks stay enabled. All workers share one temporary Admin session. Login throughput, distinct customer sessions, customer-specific order filters, payment providers, checkout throttling, static assets, browser rendering, TLS, and Internet latency are **not measured**. The initial benchmark uses Development middleware to allow local HTTP, with Release-compiled code and warning-level logs.

## Measurements and controls

Default: 1, 10, 25, 50, and 100 concurrent workers; 10 seconds per step; a 10-second unreported warmup before each scenario. `BENCH_WARMUP_SECONDS` can set a warmup of 2-30 seconds. This is a **closed-loop saturation test**: each worker waits for its response before sending the next request, with no think time. It measures delivered throughput; it does not simulate an independent arrival rate or thousands of real user sessions. Under overload, closed-loop clients slow their own request arrivals, so use a separate arrival-rate test before making production capacity guarantees.

Reports contain total and successful requests/second, p50/p95/p99/max latency, error percentage, status counts, and per-endpoint counts/errors/total latency. HTTP errors and network timeouts count as failures. Timing includes draining outstanding requests; requests time out after 10 seconds. A scenario stops ramping after errors exceed 5% or p95 exceeds five seconds.

Use **p95 below 500 ms and errors below 1%** as provisional targets, not an established business SLA. Find the highest passing concurrency for each workload; do not quote the fastest single endpoint as overall store capacity. The API, PostgreSQL, and load generator share the same computer, so resource contention and generator limits influence the result. Short tests do not establish sustained capacity or a production maximum.

For a longer repeat (up to 200 workers and 60 seconds per stage):

```powershell
$env:BENCH_SECONDS = '30'
$env:BENCH_CONCURRENCY = '25,50,100,200'
try { node scripts/Benchmark-Api.mjs }
finally {
    Remove-Item Env:BENCH_SECONDS -ErrorAction SilentlyContinue
    Remove-Item Env:BENCH_CONCURRENCY -ErrorAction SilentlyContinue
}
```

Set `BENCH_POSTGRES_BIN` if PostgreSQL is installed elsewhere. No target URL or connection-string override is supported intentionally.

Set `BENCH_VERIFY=true` to also create a separate `shelfsync_test` database in the disposable cluster and run the existing .NET test suite before the load. Test output is saved as `tests.log`.

## Connection budget

The initial load test exposed PostgreSQL `53300` (too many clients) errors: Identity and inventory were each using an independent pool. They now use the same registered `NpgsqlDataSource`, following [Npgsql's shared data-source guidance](https://www.npgsql.org/doc/basic-usage.html) and [EF's external data-source configuration](https://www.npgsql.org/efcore/). The default maximum is 80 connections for the whole API instance, leaving headroom on a typical 100-connection database. Explicit `Maximum Pool Size` in the connection string takes precedence. Dependency injection owns and disposes the shared pool.

This is a per-process budget. When deploying multiple API instances, divide the database connection budget across all of them and reserve room for other database clients. Raising PostgreSQL's connection limit alone is not a substitute for measuring database resources and bounding application demand.

## Before a production capacity claim

Repeat on the intended hosting hardware with the load generator on a separate machine, production-like data volumes and network/TLS configuration. Use many customer and associate identities, realistic browse/cart/order mixes, think time, and a controlled arrival-rate ramp. Monitor application CPU/memory/GC, PostgreSQL locks/query latency/connections, and load-generator utilization. Run a sustained soak and recovery test, and test payment correctness separately with the provider's test environment rather than stressing its public API.

Translate measured throughput to active users only after measuring user behavior: for example, 1,000 requests/second at an average of two API calls per active user per second suggests 500 active users **for that mix and those assumptions**. It says nothing about registered users or peak login bursts.
