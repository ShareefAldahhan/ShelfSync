import { request, expect } from '@playwright/test'
import { mkdir } from 'node:fs/promises'

export default async function setup() {
  await mkdir('../artifacts/browser/auth', { recursive: true })
  for (const role of ['customer', 'associate']) {
    const client = await request.newContext({ baseURL: process.env.DEMO_BASE_URL })
    const info = await client.get('/__demo/info')
    expect((await info.json()).simulatedPayments).toBe(true)
    const session = await (await client.get('/api/auth/session')).json()
    const response = await client.post('/api/auth/login', {
      headers: { 'X-CSRF-TOKEN': session.token },
      data: { username: `demo-${role}`, password: 'ShelfSync-Demo-Only-2026!' },
    })
    expect(response.status()).toBe(200)
    await client.storageState({ path: `../artifacts/browser/auth/${role}.json` })
    await client.dispose()
  }
}
