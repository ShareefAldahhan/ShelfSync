# Verification - October 26, 2026

## Changes

- Added a one-command isolated demo with four roles, 60 fictional products and an explicitly simulated checkout.
- Added server-side catalog search, department filters, price sorting and pagination. Cart details load independently of the visible catalog page.
- Added browser coverage of account permissions, unpaid picking rejection, checkout/receipt, duplicate signed events and complete pickup.
- Fixed account switching retaining the previous account's payment URL.
- Recorded the actual application workflow in [MP4](demo/app-walkthrough.mp4), using the isolated simulator.

## Local results

The full isolated release gate passed: **143 backend tests and 4 frontend tests**, plus published lifecycle/account/security checks, build/lint, repeatable migrations and backup/restore. Four browser scenarios passed against a fresh demo database. The simulator assembly remains separate from the release API.

Actual Stripe CLI delivery also passed against a separate disposable database: signature validated, one event stored, payment PAID, authoritative test session USD 2.97 matched and `livemode=false`. An invalid signature returned HTTP 400. This check used Stripe's official sandbox fixture with a test card token; no real payment was made. CLI/API secrets are excluded from the report and repository.

The original October 25 payment was confirmed by reconciliation. The October 26 test closes the separate local signed-webhook-delivery verification gap; it does not claim that a public deployment or live merchant payment was tested.

## Scope

The app remains locally deployable. No explanatory website, paid resource, live key or external email provider is enabled. Load-test results remain the October 25 measurements; pagination changes have not been re-benchmarked, and no new throughput claim is made.
