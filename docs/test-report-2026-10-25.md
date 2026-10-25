# ShelfSync verification — 25 October 2026

Result: 116 automated tests passed, zero failures or skips in the final run. After one expiry race was fixed, two clean full release gates passed. All additive migrations also reapplied successfully. The fixed release is active locally.

## Automated checks

| Area | Result |
| --- | --- |
| Reservation/POS concurrency, stock invariants, rollback and replay | Passed |
| Cancellation, picking, shortages, fulfillment, expiry | Passed |
| Receiving, replenishment, return bounds, picked cancellation | Passed |
| Customer ownership and associate shopping/picking restrictions | Passed |
| Whole-cart checkout, captured prices and linked receipts | Passed |
| Paid-only picking/queues, failure/expiry/cancellation hold release | Passed |
| Delayed payments, retries, refunds, late success requiring review | Passed using fake payment gateway |
| Stripe SDK signature checks, tampered/stale/live-event rejection | Passed |
| Registration, role changes, disablement, resets, recovery and lockout | Passed |
| Published API workflows, CSRF, signed cookies and access denials | Passed |
| Production plain-HTTP rejection | Passed on isolated verification server |
| Additive migration installation and second application | Passed |
| Frontend TypeScript/production build and lint | Passed |
| npm production dependency advisory audit | No reported vulnerabilities |
| Backend direct/transitive NuGet advisory audit | No reported vulnerabilities |

The release gate used a disposable, password-protected PostgreSQL instance. It did not reset the development database or change existing customer orders for test purposes. External Stripe calls were disabled inside automated HTTP checks; payment state-machine tests use an injected fake provider and signature tests use the official SDK.

## Issue found and corrected

The expiry worker collected a candidate before another transaction removed its order and inventory. It then treated the stale candidate as a missing-inventory failure. The fix checks whether the order also disappeared and skips that removed candidate, while retaining the error if an existing order truly lacks inventory. A deterministic regression test holds the inventory lock, lets the worker block, removes the order/inventory and verifies the sweep completes safely. Checkout hold tests now scope expiry sweeps to their own fixture product. Test cleanup retains inventory-before-order lock ordering.

## Browser and running application

Checked the live Customer catalog, product details, cart routing and checkout page without changing cart quantities or submitting checkout. Customer cards/details expose Add to cart and omit direct reservation controls. The cart shows the server-checkout flow and test-mode notice. Unpaid checkout does not show a receipt. Browser warning/error collection was empty during these checks.

Phone-width testing at 390 × 844 showed a single-column checkout, readable order reference and total, and accessible payment/refresh/cancellation controls. The normal viewport was restored afterward. Screenshots are retained privately under artifacts/test-mobile-payment.png and artifacts/test-mobile-payment-actions.png.

Live readiness passed. Anonymous catalog and payment configuration endpoints returned 401. The API retained no-store and Content-Security-Policy headers.

A read-only Stripe request authenticated successfully. The existing Checkout Session was test mode, open and unpaid, with an amount and USD currency matching the stored payment. The local Stripe forwarding process was running. No API keys, webhook secrets or payment details were printed in the report.

## Remaining manual/external coverage

A complete hosted Stripe checkout submission was not performed. The user must complete a sandbox payment; afterward verify the real webhook-to-PAID transition, automatically displayed full-cart receipt and associate queue. Automated tests cover these transitions with a fake gateway, which is not a replacement for that external end-to-end test. No real payment was initiated.

Physical scanner hardware, external email delivery, phone-camera capture, live-money processing, production hosting/TLS, Docker/remote CI execution, load testing and an independent penetration test are not claimed. Some are deliberately outside the local release scope. No reported dependency advisories is not a security certification.

## Reproduce

From the repository root:

```powershell
pwsh -File scripts/Verify-Release.ps1 -PublishDirectory artifacts/full-verification
npm --prefix frontend audit --omit=dev --audit-level=moderate
dotnet list backend/ShelfSync.Api/ShelfSync.Api.csproj package --vulnerable --include-transitive
```

The separate publish directory avoids overwriting files used by the running local app. Keep Stripe secrets private, keep real keys disabled, and leave the local forwarding window running for manual sandbox checkout.


## User-completed sandbox checkout follow-up

The user subsequently completed a hosted Stripe test payment. A read-only provider query confirmed payment_status=paid, livemode=false, and amount/currency matching the local record. ShelfSync stored PAID, retained a live stock hold, and associated the order with a Customer account, making it eligible for the paid picking queue. The browser showed the automatically unlocked receipt and Print receipt control. The stock stage remained RESERVED pending picking, as designed. No signed payment_events row was observed for this payment during verification, so direct webhook delivery is not claimed; authoritative provider reconciliation confirmed payment successfully. The screenshot of the Stripe dashboard reports CAD, while the verified order payment is USD; exact conversion/fee details were not inspected.
