import { secureFetch } from './session'
export type Product = { id: string; sku: string; name: string; priceCents: number; shelfUnits: number; pickedUnits: number; safetyBuffer: number; quarantined: boolean }
export type Order = { id: string; productId: string; sku: string; name: string; quantity: number; state: string; expiresAt: string; createdAt: string; updatedAt: string; reason: string | null; paymentState?:string|null; customerReservation?:boolean; canPick?:boolean }
export type Operation = { key: string; url: string; body?: object }
export async function mutate(operation: Operation) {
  const response = await secureFetch(operation.url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operation.key }, body: operation.body ? JSON.stringify(operation.body) : undefined })
  if (response.status >= 500) throw new Error('Uncertain result')
  const text = await response.text()
  let body: { error?: string; [key: string]: unknown } = {}
  try { body = text ? JSON.parse(text) : {} } catch { if (response.ok) throw new Error('Unexpected success response') }
  if (!response.ok && !body.error) body.error = response.status === 403 ? 'Your account does not have permission for this action.' : response.status === 401 ? 'Your session expired. Sign in again.' : `Request rejected (HTTP ${response.status}).`
  return { ok: response.ok, body, status: response.status }
}
