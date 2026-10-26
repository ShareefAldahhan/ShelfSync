# Isolated local demo and browser tests

## Start with one command

From the repository root, with Node.js 24, .NET 10 SDK and PostgreSQL 18 tools installed:

```powershell
node scripts/Demo.mjs
```

Open **http://127.0.0.1:5092**. The script builds the frontend and demo host, initializes a fresh password-protected PostgreSQL cluster on loopback port 55442, applies the schema/migrations, and seeds 60 fictional products and four accounts. It does not ask for or change the store's saved database/password configuration. Set `DEMO_POSTGRES_BIN` to the PostgreSQL 18 bin directory if it is installed elsewhere.

| Account | Role |
|---|---|
| demo-customer | Customer |
| demo-associate | Associate |
| demo-manager | Manager |
| demo-admin | Recovery Admin |

All four use **ShelfSync-Demo-Only-2026!**. These public credentials are only for this disposable, loopback-only demo. Stop with Ctrl+C; the host and its database stop. A new invocation creates a new database, so there is no reset operation against the store. Stopped fixtures/logs remain under ignored `artifacts/demo-*` for diagnostics.

The standalone demo supports Windows and PostgreSQL 18's standard installation. On Linux, set `DEMO_POSTGRES_BIN` if the tools are elsewhere. Ports 5092 and 55442 must be free.

## Simulated checkout

The demo banner and checkout page explicitly identify simulated payments. No Stripe request, real card or money is involved in this mode. Checkout uses the application's real authentication, cart reservation, amount checks, payment state machine, webhook signature handler, receipt and picking code. A demo-only gateway and local checkout page supply the provider result.

The simulator is in `tools/ShelfSync.DemoHost`, which is not a dependency of the production API and is not copied into its Docker image or release package. The actual API still accepts only Stripe test sessions. The demo frontend permits a same-origin simulated checkout URL only after detecting the demo host and matching its exact route format. Tests cover that redirect boundary.

The application checks the requested demo database identity before bootstrap and the host checks the connected database/user/port before seeding accounts. A mismatched store connection fails startup. The demo host binds only to 127.0.0.1.

## Automated browser tests

Install the browser once:

```powershell
npm --prefix frontend ci
npm --prefix frontend exec playwright install chromium
```

Then run:

```powershell
node scripts/Demo.mjs --test
```

The runner creates a fresh demo, runs Playwright, and stops the demo afterward. The scenarios cover server-side search/sort/pagination, cart products across catalog pages, Associate UI/API restrictions, unpaid picking rejection, simulated checkout, signed HTTP event ingestion, receipt display, duplicate events, switching accounts and completing pickup with correct final inventory.

The HTML report, failure traces/screenshots and recordings are under ignored `artifacts/browser/`. The [app recording](demo/app-walkthrough.mp4) shows the customer-to-associate handoff at reduced playback speed. It uses simulated payments and fictional data. It is a recording of the real app screens, not a hosted explanatory site.

CI uses its own pre-provisioned loopback `shelfsync_test` database through `SHELFSYNC_DEMO_TEST_CONNECTION`; other database names and remote hosts are rejected. This optional variable is for disposable test environments, not store configuration. CI sets `DEMO_RECORD=0` for normal speed and retains video only for failures.

## Actual Stripe sandbox webhook check

This separate check uses a privately configured `sk_test_` key and the installed Stripe CLI:

```powershell
node scripts/Demo.mjs --stripe-webhook
```

It creates an isolated database, starts the real API and a temporary CLI listener, and runs a customized copy of Stripe's official `checkout.session.completed` fixture. This creates sandbox product/price/payment/session records in the test account. It sends no real card or live payment. The fixture's amount and metadata match an isolated USD 2.97 order.

Success requires a signature-validated event row in the database, PAID state, authoritative Stripe session amount/currency/metadata checks, and rejection of an invalid signature. Reconciliation alone cannot satisfy the event-row check. The listener and API stop afterward. Signing/API secrets stay in process memory; listener and fixture logs are redacted. `STRIPE_CLI_PATH` can select the CLI executable.

On October 26, 2026 this check passed with one stored signed event, `livemode=false`, USD 2.97, PAID state and HTTP 400 for an invalid signature. See [verification results](verification-2026-10-26.md). This verifies Stripe CLI delivery to a local API, not a public merchant deployment.
