import { secureFetch } from './session'
export type Product = { id: string; sku: string; name: string; priceCents: number; shelfUnits: number; pickedUnits: number; safetyBuffer: number; quarantined: boolean }
export type Order = { id: string; productId: string; sku: string; name: string; quantity: number; state: string; expiresAt: string; createdAt: string; updatedAt: string; reason: string | null }
export type Operation = { key: string; url: string; body?: object }
export async function mutate(operation: Operation) {
  const response = await secureFetch(operation.url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operation.key }, body: operation.body ? JSON.stringify(operation.body) : undefined })
  if (response.status >= 500) throw new Error('Uncertain result')
  const body = await response.json()
  return { ok: response.ok, body, status: response.status }
}
