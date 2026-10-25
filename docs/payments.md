> Current workflow (25 October 2026): checkout now happens before picking and covers the full cart. See [checkout-first](checkout-first.md). The original payment-after-picking design below is retained as historical context for existing individually paid orders.

# Stripe hosted checkout — test mode only

ShelfSync uses the official Stripe.net SDK and Stripe-hosted Checkout. This integration accepts only test keys and test Checkout Sessions; it cannot collect real money. USD pricing is preserved. Canada is the selected merchant country; payment-method eligibility is determined by the actual Stripe account, currency, customer and Dashboard settings. No application can promise every payment method. Dynamic payment methods are left to Stripe; no card number, CVC or bank credential is collected or stored by ShelfSync.

## User flow

Reserve items, let staff pick the order, then open **Reservations → Payment / status**. Only the order owner can start checkout. Staff may read/refresh status but cannot pay another user's order. The server uses quantity and price captured on the order and passes the resulting USD total to Stripe. Each picked product order is paid separately, including lines from a pickup cart. Multi-line unified payment is not implemented.

Checkout redirects to an allowlisted HTTPS checkout.stripe.com URL. Returning to ShelfSync does not mark anything paid. The payment page reads the database, polls pending status and offers **Refresh payment confirmation** to retrieve the authoritative session from Stripe if a webhook is delayed. It checks test mode, session binding, payment metadata, amount and currency before confirming. Staff cannot fulfill an order with a pending, failed, refunded or review payment. Once payments are enabled, owned orders require confirmed payment before fulfillment. Legacy ownerless orders remain compatible with the manual demo workflow.

Asynchronous payment methods remain PROCESSING until confirmed. Stock is already PICKED and is not subject to reservation expiry. Active payment attempts block picked-order cancellation. The durable payment ID is the Stripe idempotency key; simultaneous starts and network retries reuse the same checkout. Expiry parameters are captured from the durable creation time so retries send consistent arguments. Unknown attempts older than 20 hours require reconciliation instead of risking a new charge after Stripe's idempotency retention window.

## Private setup

For the original local machine, `pwsh -File scripts/Connect-StripeTest.ps1` provides a single private prompt for a sk_test_ key. It uses STRIPE_API_KEY only in the setup process to obtain the CLI signing secret automatically, saves the two secrets through stdin to user-secrets, restarts the already-published local app and keeps webhook forwarding in that visible window. Close the listener to stop forwarding. It does not initiate any payment. The manual procedure below is also supported.

1. Apply migration 004 through the approved schema-owner helper:

```powershell
pwsh -File scripts/Apply-Migrations.ps1 -EnableAccounts -EnableProfileEditing -EnableStoreOperations -EnablePayments
```

It adds payments and payment_events and grants the restricted app SELECT/INSERT/UPDATE on payments and SELECT/INSERT on payment_events. It does not change existing prices, inventory or order ownership.

2. In a private terminal, install/use the official Stripe CLI, run `stripe login`, then keep this listener running:

```powershell
stripe listen --events checkout.session.completed,checkout.session.async_payment_succeeded,checkout.session.async_payment_failed,checkout.session.expired --forward-to localhost:5000/api/payments/stripe-webhook
```

3. Obtain your secret **test** API key from the Stripe sandbox/test dashboard. Run `pwsh -File scripts/Set-Payments.ps1` and enter that key and the listener's whsec_ signing secret privately. The script sends secrets to user-secrets through stdin instead of process arguments. Never paste credentials into chat or commit them. The publish package excludes user secrets. Keep local configuration and the CLI account secure.

4. Restart ShelfSync. A private fixed Stripe:BaseUrl controls return URLs; it is never derived from a request Host header. HTTP is allowed only for loopback return origins; other origins require HTTPS. Real keys remain rejected by the application's Enabled check.

5. Use Stripe test payment details only on the hosted page. Test success, declines, authentication challenges, delayed payment where available, abandoned checkout and duplicate notifications. These manual/provider tests require actual test-account credentials and are separate from automated fake-gateway tests.

## Webhook and administration

The webhook is the only API write exempt from CSRF. It instead verifies the exact raw body with Stripe's official signature verifier and a five-minute timestamp tolerance. Only test checkout event types are processed; connected-account and live events are rejected. The handler retrieves the session using the server's own key, verifies its binding, and records event IDs transactionally. Duplicate and out-of-order events cannot downgrade confirmed success. Other application writes retain CSRF, authentication, role policies and rate limiting. Do not enable request-body or credential logging.

Admins open **Payments** for recent attempts and provider reconciliation. If a network interruption prevents the session ID from reaching ShelfSync, find the matching session in the Stripe test dashboard and reconcile its cs_test_ ID against the recorded payment ID. A session for another payment, currency or amount is rejected. A stale unresolved READY record must not be deleted blindly.

Stock returns do not issue financial refunds. Refund through the Stripe **test dashboard**, then refresh/reconcile provider status. A confirmed full refund becomes REFUNDED and permits picked cancellation; partial refunds become REVIEW and block handover. ShelfSync never initiates a refund automatically. Disputes, chargeback workflows and live financial accounting are not implemented. Missing provider setup fails closed rather than pretending to pay.

## Security verification and limits

Automated tests use an injected fake provider to check ownership, server prices, concurrent idempotency, delayed confirmation, event replay, amount/currency mismatch, live-mode rejection, valid/invalid/stale webhook signatures, fulfillment/cancellation safeguards and refunds. The real SDK verifies signatures. Published checks verify access boundaries and disabled-configuration behavior without making external payment calls. This is not an independent penetration test, PCI certification or proof of a real Stripe transaction.

Live deployment is deliberately disabled. It would need separate merchant onboarding, applicable tax/shipping decisions, refund/dispute operations, durable monitoring/reconciliation, HTTPS hosting and a production security review. No live money movement is authorized or performed by this work.

Official references: [Stripe Checkout Sessions](https://docs.stripe.com/api/checkout/sessions/create), [dynamic payment methods](https://docs.stripe.com/payments/payment-methods/dynamic-payment-methods), [webhook verification and local forwarding](https://docs.stripe.com/webhooks?lang=dotnet).


Checkout UI update — 25 October 2026: the payment page uses a two-column order/checkout layout with an explicit amount on the primary button. Receipt controls are hidden before PAID; verified PAID status opens the printable receipt automatically. The customer checkout and absence of an unpaid receipt were visually checked on the running app. No payment was initiated as part of that visual check.
