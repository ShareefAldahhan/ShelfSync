import { spawn, spawnSync } from 'node:child_process'
import { readFileSync, writeFileSync, openSync, closeSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { randomUUID, randomBytes, pbkdf2Sync } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'

// Called only inside Demo.mjs's disposable database lifetime.
export async function verifyStripeWebhook({ root, run, connection, env, sql }) {
  const settings = JSON.parse(readFileSync(join(process.env.APPDATA, 'Microsoft/UserSecrets/f1743222-090c-4111-8a6c-12e064007dba/secrets.json'), 'utf8').replace(/^\uFEFF/, ''))
  const secretKey = settings['Stripe:SecretKey'] ?? settings.Stripe?.SecretKey
  if (!secretKey?.startsWith('sk_test_')) throw new Error('A privately configured Stripe TEST key is required; live keys are refused.')
  const stripe = process.env.STRIPE_CLI_PATH ?? join(process.env.APPDATA, 'npm/node_modules/@stripe/cli/node_modules/@stripe/cli-win32-x64/bin/stripe.exe')
  if (!existsSync(stripe)) throw new Error('Stripe CLI required; set STRIPE_CLI_PATH to its executable.')
  const origin = 'http://127.0.0.1:5092'
  const stripeEnv = { ...env, STRIPE_API_KEY: secretKey }
  const redact = text => text.replace(/(?:whsec_|sk_test_)[A-Za-z0-9_]+/g, '[redacted]')
  let listener, server, webhookSecret = '', listenerText = ''
  const log = openSync(join(run, 'stripe-api.log'), 'a')
  try {
    listener = spawn(stripe, ['listen', '--events-from', '@self', '--events', 'checkout.session.completed', '--forward-to', origin + '/api/payments/stripe-webhook'], { cwd: root, env: stripeEnv, windowsHide: true })
    const consume = chunk => {
      const text = chunk.toString()
      listenerText += text
      webhookSecret = listenerText.match(/whsec_[A-Za-z0-9_]+/)?.[0] ?? ''
      writeFileSync(join(run, 'stripe-listener.log'), redact(listenerText))
    }
    listener.stdout.on('data', consume); listener.stderr.on('data', consume)
    for (let attempt = 0; attempt < 60 && !webhookSecret; attempt++) {
      if (listener.exitCode !== null) throw new Error('Stripe listener exited. Inspect the redacted listener log.')
      await delay(500)
    }
    if (!webhookSecret) throw new Error('Stripe listener did not become ready.')
    const salt = randomBytes(16), header = Buffer.alloc(13)
    header[0] = 1; header.writeUInt32BE(2, 1); header.writeUInt32BE(100000, 5); header.writeUInt32BE(16, 9)
    const hash = Buffer.concat([header, salt, pbkdf2Sync(randomBytes(32), salt, 100000, 32, 'sha512')]).toString('base64')
    server = spawn('dotnet', ['backend/ShelfSync.Api/bin/Release/net10.0/ShelfSync.Api.dll', '--urls', origin], {
      cwd: root, windowsHide: true, stdio: ['ignore', log, log], env: { ...env,
        ASPNETCORE_ENVIRONMENT: 'Development', ConnectionStrings__ShelfSync: connection,
        SHELFSYNC_DEMO_CONNECTION: connection, Operator__Username: 'webhook-verification', Operator__PasswordHash: hash,
        Stripe__Enabled: 'true', Stripe__SecretKey: secretKey, Stripe__WebhookSecret: webhookSecret,
        Stripe__BaseUrl: origin, Hosting__Enabled: 'false', Logging__LogLevel__Default: 'Warning' }
    })
    let ready = false
    for (let attempt = 0; attempt < 60; attempt++) {
      if (server.exitCode !== null) throw new Error('Webhook verification API exited; inspect its log.')
      try { if ((await fetch(origin + '/health/ready', { signal: AbortSignal.timeout(2000) })).ok) { ready = true; break } } catch { /* startup */ }
      await delay(500)
    }
    if (!ready) throw new Error('Webhook verification API did not become ready.')
    const order = randomUUID(), payment = randomUUID()
    sql(`INSERT INTO orders(id,product_id,quantity,unit_price_cents,state,expires_at,owner_user_id)
      SELECT '${order}','00000000-0000-4000-8000-000000000001',1,297,'RESERVED','infinity',"Id" FROM app_users WHERE "UserName"='webhook-verification';
      INSERT INTO payments(id,order_id,owner_user_id,amount_cents,currency,state)
      SELECT '${payment}','${order}',"Id",297,'usd','READY' FROM app_users WHERE "UserName"='webhook-verification';`)
    const response = await fetch('https://raw.githubusercontent.com/stripe/stripe-cli/master/pkg/fixtures/triggers/checkout.session.completed.json')
    if (!response.ok) throw new Error('Official Stripe CLI fixture download failed.')
    const fixture = await response.json()
    fixture.fixtures.find(item => item.name === 'product').params.name = 'ShelfSync webhook verification (test only)'
    fixture.fixtures.find(item => item.name === 'price').params.unit_amount = '297'
    const checkout = fixture.fixtures.find(item => item.name === 'checkout_session').params
    checkout.line_items[0].quantity = 1
    checkout.metadata = { payment_id: payment, order_id: order }
    checkout.success_url = origin; checkout.cancel_url = origin
    delete checkout.payment_intent_data
    fixture.fixtures.find(item => item.name === 'payment_method').params.billing_details = { name: 'ShelfSync fixture', email: 'demo@example.com' }
    fixture.fixtures.find(item => item.name === 'payment_page_confirm').params.expected_amount = 297
    const fixtureFile = join(run, 'stripe-checkout-fixture.json')
    writeFileSync(fixtureFile, JSON.stringify(fixture, null, 2))
    console.log('Generating a Stripe sandbox fixture and waiting for signed CLI delivery...')
    const fixtureRun = spawnSync(stripe, ['fixtures', fixtureFile], { cwd: root, env: stripeEnv, windowsHide: true, encoding: 'utf8' })
    writeFileSync(join(run, 'stripe-fixture.log'), redact((fixtureRun.stdout ?? '') + (fixtureRun.stderr ?? '')))
    if (fixtureRun.error || fixtureRun.status !== 0) throw new Error('Stripe sandbox fixture failed; inspect its redacted log.')
    let session = '', signedEvents = 0
    for (let attempt = 0; attempt < 60; attempt++) {
      const status = sql(`SELECT p.state||'|'||(SELECT count(*) FROM payment_events e WHERE e.payment_id=p.id)||'|'||coalesce(p.stripe_session_id,'') FROM payments p WHERE p.id='${payment}';`)
      const matched = status.match(/PAID\|(\d+)\|(cs_test_[A-Za-z0-9_]+)/)
      if (matched && Number(matched[1]) > 0) { signedEvents = Number(matched[1]); session = matched[2]; break }
      await delay(500)
    }
    if (!session) throw new Error('No signature-validated payment event was stored. Inspect redacted logs; reconciliation alone is insufficient.')
    const stripeResponse = await fetch('https://api.stripe.com/v1/checkout/sessions/' + session, { headers: { Authorization: 'Bearer ' + secretKey } })
    if (!stripeResponse.ok) throw new Error('Could not verify the authoritative sandbox session.')
    const snapshot = await stripeResponse.json()
    if (snapshot.livemode !== false || snapshot.payment_status !== 'paid' || snapshot.amount_total !== 297 || snapshot.currency !== 'usd' || snapshot.metadata.payment_id !== payment)
      throw new Error('Sandbox session did not match the isolated order.')
    const invalid = await fetch(origin + '/api/payments/stripe-webhook', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Stripe-Signature': 'invalid' }, body: '{}' })
    if (invalid.status !== 400) throw new Error('Invalid signature was not rejected.')
    const report = { verifiedAt: new Date().toISOString(), mode: 'Stripe sandbox', amountCents: 297, currency: 'usd', livemode: false,
      paymentState: 'PAID', storedSignedEvents: signedEvents, invalidSignatureStatus: invalid.status,
      transport: 'Stripe CLI forwarding to the real API; authoritative session retrieved with Stripe SDK; isolated database', sessionId: session }
    writeFileSync(join(run, 'stripe-webhook-result.json'), JSON.stringify(report, null, 2))
    console.log('PASS: actual Stripe sandbox event delivered, signature verified, payment PAID, authoritative USD 2.97 matched, invalid signature rejected.')
    console.log('Evidence: ' + join(run, 'stripe-webhook-result.json'))
  } finally {
    for (const process of [server, listener]) if (process && process.exitCode === null) {
      const exited = new Promise(resolvePromise => process.once('exit', resolvePromise))
      process.kill(); await Promise.race([exited, delay(5000)])
    }
    closeSync(log)
  }
}
