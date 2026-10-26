import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import vm from 'node:vm'
import ts from 'typescript'

const source = readFileSync(new URL('../src/checkout.ts', import.meta.url), 'utf8')
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText

function checkoutModule(secureFetch = () => { throw new Error('Unexpected request') }) {
  const destinations = []
  const context = {
    exports: {}, URL, Intl,
    require: name => {
      assert.equal(name, './session')
      return { secureFetch }
    },
    window: { location: { origin: 'http://127.0.0.1:5092', assign: url => destinations.push(url) } },
  }
  vm.runInNewContext(compiled, context)
  return { ...context.exports, destinations }
}

test('checkout permits only the hosted Stripe HTTPS destination', () => {
  const checkout = checkoutModule()
  checkout.redirectToCheckout('https://checkout.stripe.com/c/pay/test-session')
  assert.deepEqual(checkout.destinations, ['https://checkout.stripe.com/c/pay/test-session'])
  for (const url of [
    'http://checkout.stripe.com/pay',
    'https://checkout.stripe.com.evil.example/pay',
    'https://checkout.stripe.com@evil.example/pay',
    'https://user:password@checkout.stripe.com/pay',
    'https://checkout.stripe.com:8443/pay',
    'javascript:alert(1)', '/relative',
  ]) assert.throws(() => checkout.redirectToCheckout(url))
  assert.equal(checkout.destinations.length, 1)
})

test('receipts require server-confirmed payment and forward cancellation', async () => {
  const controller = new AbortController()
  let paymentState = 'OPEN'
  const checkout = checkoutModule(async (url, options) => {
    assert.equal(url, '/api/orders/order-1/receipt')
    assert.equal(options.signal, controller.signal)
    return { ok: true, json: async () => ({ paymentState }) }
  })
  await assert.rejects(checkout.fetchPaidReceipt('order-1', controller.signal), /after payment is confirmed/)
  paymentState = 'PAID'
  assert.equal((await checkout.fetchPaidReceipt('order-1', controller.signal)).paymentState, 'PAID')
})

test('receipt HTTP errors are surfaced instead of displaying a receipt', async () => {
  const checkout = checkoutModule(async () => ({ ok: false }))
  await assert.rejects(checkout.fetchPaidReceipt('order-1', new AbortController().signal), /Unable to load receipt/)
})

test('local simulated checkout requires explicit demo detection and an exact same-origin path', () => {
  const checkout = checkoutModule()
  const valid = 'http://127.0.0.1:5092/__demo/checkout/cs_test_demo_' + 'a'.repeat(32)
  assert.throws(() => checkout.redirectToCheckout(valid))
  checkout.redirectToCheckout(valid, true)
  assert.equal(checkout.destinations[0], valid)
  for (const bad of [valid + '?redirect=elsewhere', valid.replace(':5092', ':5000'), valid.replace('/__demo/checkout/', '/other/')])
    assert.throws(() => checkout.redirectToCheckout(bad, true))
})
