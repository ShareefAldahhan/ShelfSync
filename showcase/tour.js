const steps = {
  cart: ['CART · NO STOCK HOLD', 'Browsing is not a reservation.', "Adding an item updates the customer's cart. Stock and prices are checked by the server when checkout begins.", 'Shelf quantity is unchanged; nothing is held yet.', 'A saved request identifier makes an uncertain checkout safe to retry.'],
  hold: ['RESERVED · PAYMENT OPEN', 'Make the stock decision atomically.', 'Checkout locks inventory in a consistent order, checks every line, captures server prices and creates the stock holds.', 'Holds reduce sellable availability. They do not physically remove shelf units.', 'While the provider result is uncertain, protected holds prevent the same units being sold again.'],
  paid: ['RESERVED · PAYMENT PAID', 'Payment and pickup are separate states.', 'A verified provider result confirms the payment. The receipt becomes available and the customer order enters the picking queue.', 'The reservation stays protected until staff pick the items.', 'Signed-event verification and provider reconciliation check payment identity, currency and amount. A redirect alone is not proof of payment.'],
  picked: ['PICKED · PAYMENT PAID', 'Give associates an actionable order.', 'An authorized associate picks an eligible paid customer order and can inspect barcode, bin location and item history.', 'Picking moves units from shelf stock to picked stock in the same transaction.', 'The API rechecks eligibility under locks; hiding a button is not the authorization boundary.'],
  fulfilled: ['FULFILLED · PAYMENT PAID', 'Finish the physical handoff.', 'Pickup completion records fulfillment and the corresponding inventory event.', 'Picked stock decreases when the order leaves the store.', 'Idempotent replay prevents a repeated request from subtracting stock twice. Stock returns and payment refunds remain separate operations.'],
  expired: ['CHECKOUT EXPIRED · HOLD RELEASED', 'Release only after the uncertainty ends.', 'Provider-confirmed cancellation or expiry allows an abandoned checkout hold to be released.', 'Unpicked stock becomes available again; physical shelf units were never removed.', 'A closed browser or a timed-out HTTP request is not evidence that payment failed. Reconciliation resolves uncertain attempts.']
};
document.querySelectorAll('[data-step]').forEach(button => button.addEventListener('click', () => {
  const values = steps[button.dataset.step];
  if (!values) return;
  ['tour-state', 'tour-title', 'tour-description', 'tour-inventory', 'tour-safety'].forEach((id, index) => {
    document.getElementById(id).textContent = values[index];
  });
  document.querySelectorAll('[data-step]').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
}));
