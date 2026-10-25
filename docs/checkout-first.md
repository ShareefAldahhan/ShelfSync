# Checkout before picking

The customer flow is Add to cart → Checkout → Stripe test payment → staff pick → pickup. Adding to cart does not hold inventory. The separate Reserve for pickup controls are removed. Cart checkout validates stock and captured USD prices, creates all temporary reservation lines atomically, and opens one Stripe Checkout Session for the full cart. A cart has at most 30 distinct products and 100 units per product.

## Stock and money boundaries

Cart checkout uses a saved per-account request key. Its stock-reservation step replays existing order IDs; its payment step reuses a durable payment ID and consistent Stripe idempotency parameters. A network retry does not create another cart or charge. Replaying a cart request that is already paid returns the confirmed order instead of starting another payment.

Before requesting Stripe, inventory and order rows are locked in stable order and live holds are rechecked using database time. While provider creation may have succeeded, RESERVED rows use an internal infinity expiry. Availability still counts those reservations, but the old reservation sweeper cannot release them prematurely. This is a safety fence, not an unlimited customer checkout: Stripe Sessions expire after one hour, signed events or a reconciliation sweep release expired/failed holds. The application never frees stock based only on a browser redirect or local timer while a payment could still succeed.

A background worker checks up to 20 unresolved payments every minute. A provider outage keeps stock protected and retries later. READY intents with unknown provider outcomes older than 20 hours require administrator reconciliation; do not delete them or release their stock blindly. Delayed payment methods remain processing and keep their holds until the provider confirms success or terminal failure. This may last longer than the ordinary Checkout window. Local test credentials and webhook forwarding remain required.

Confirmed payment keeps the existing RESERVED stock stage while marking payment PAID for every linked order. Customer UI displays PAID — Awaiting picking. Paid reservations do not time out; picking moves shelf stock to picked as before. Associate queues show only payment-confirmed orders when payments are enabled. The picking service checks payment while holding the inventory/order locks, so a stale screen cannot bypass the gate. Manager/Admin views retain visibility of unpaid/legacy orders for investigation but cannot pick unpaid customer reservations when payments are enabled. Legacy ownerless handling and pre-upgrade already-picked payments remain compatible.

Customer cancellation of a live checkout first expires the provider session, then releases all RESERVED lines only after provider confirmation. A processing or paid payment cannot be cancelled this way. Ordinary reservation cancellation and shortage reporting cannot bypass a pending payment hold. Definite provider failure/expiry releases all cart lines with audited state changes; a success arriving after stock has been released is flagged REVIEW, never automatically picked. Confirmed full refunds cancel unpicked lines; picked items still require recorded physical disposition. Paid shortage recovery restores an unlimited paid hold rather than a new unpaid 15-minute timeout.

## Receipts and existing records

One receipt shows every product linked to the payment and the server-captured cart total. Receipts appear only after PAID. Each product order retains its own picking/fulfillment/return lifecycle. A group payment is reflected on every order line through order_payment_status. Existing individually paid cart lines are kept separate; the app will not merge them into another charge.

## Additive deployment

Migration 005 adds payments.cart_id, payment_orders, an anchor-link trigger and the read-only order_payment_status view. It backfills existing single-order payment links without altering amounts, stock or order states. The application receives SELECT/INSERT on payment_orders and SELECT on the view. Apply through the explicitly approved helper with -EnableCheckoutFirst together with previous migration flags, then activate the new binary. Never rerun schema.sql on the populated development database.

## Verification

The expanded isolated suite covers full-cart totals, one provider session across cart lines, paid-only picking and queues, multi-item receipts, safe cancellation and whole-cart hold release, delayed failure, late success requiring review, expired reservations, cross-account cancellation and provider outages. It uses a fake provider for state-machine tests and the real Stripe SDK for signature verification. Actual hosted Checkout submission remains a user action; automated checks do not claim a new real or test financial transaction.
