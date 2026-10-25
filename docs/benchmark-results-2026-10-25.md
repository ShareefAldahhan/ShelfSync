# API benchmark results - October 25, 2026

Final run: `artifacts/benchmark-2026-10-25T16-14-19-943Z`. Full backend suite: **120 passed, zero skipped or failed**.

The test exposed and fixed database connection exhaustion: Identity and inventory now share one dependency-injection-owned Npgsql data source, defaulting to 80 connections. Explicit pool-size overrides are preserved and covered by four regression cases.

## Final measurements

| Workload | Concurrent workers | API calls/sec | p95 ms | p99 ms | Errors | Meets target |
|---|---:|---:|---:|---:|---:|---|
| browse | 100 | 3529.4 | 42.8 | 52.1 | 0.00% | Yes |
| browse | 200 | 3709.3 | 72.1 | 81.1 | 0.00% | Yes |
| reserve-cancel | 100 | 2771.7 | 50.8 | 92.9 | 0.00% | Yes |
| reserve-cancel | 200 | 2753.5 | 103.1 | 137.1 | 0.00% | Yes |
| hot-product | 100 | 458.6 | 270.8 | 337.6 | 0.00% | Yes |
| hot-product | 200 | 443.6 | 580.6 | 808.6 | 0.00% | No |

**274,265 measured API requests** across the final stages. Targets: p95 < 500 ms and errors < 1%. All scenarios passed at 100 workers. At 200, the one-product workload exceeded the latency target despite zero errors. These are the tested levels, not a proven maximum.

Final database checks: {"activeReservations": 0, "changedStockRows": 0, "products": 500}. All successful reservations were cancelled, and physical inventory remained unchanged.

## Conditions and interpretation

- Intel Core i5-12600KF, 16 logical CPUs, approximately 32 GB RAM; Windows, Node.js 24.18.0, PostgreSQL 18, .NET 10 Release build.
- 20 seconds per measured stage, with 10-second warmup per scenario. This is a short saturation test, not a sustained production soak.
- 500 fictional products and 5,000 initial cancelled orders. Write scenarios accumulate additional history.
- One shared authenticated Admin session; closed-loop workers with no think time. Reservation/cancellation performs two API calls per workflow.
- API, PostgreSQL, and generator share this machine. Loopback HTTP, Development middleware, warning-level logs; disposable database-owner login. No real orders, production database, or Stripe payment requests were used.
- Browser rendering, TLS, distinct customer sessions, payment/login rate limits, and production hosting capacity are not covered. Do not equate concurrent workers with real shoppers.

## Initial failure retained for comparison

`artifacts/benchmark-2026-10-25T16-06-20-981Z`: independent account/inventory pools exhausted PostgreSQL connections (`53300`). At 100 workers, reserve/cancel returned 31 HTTP 500s (0.65%); hot-product errors reached 77.36%. Some cancellations failed, leaving active reservations in that disposable cluster, and the invariant check correctly failed.

An intermediate shared-pool run eliminated request errors but exhausted maintenance connection headroom. A configuration-key detection bug was then fixed and tested. Only the final run above passed the complete stock/order verification. Different warmup lengths and measurement windows mean the runs are not a controlled throughput A/B comparison.

## Reproduce and next capacity work

See [benchmarking instructions](benchmarking.md). For production sizing, repeat on target hosting with a separate load generator, many real-role test sessions, realistic arrival rates and data volumes, database/CPU/GC monitoring, and a sustained soak. The current full-catalog response is unpaginated; pagination is a candidate to profile as catalog sizes grow.
