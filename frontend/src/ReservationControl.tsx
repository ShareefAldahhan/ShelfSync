import { secureFetch } from './session'
import { useRef, useState } from 'react'

type Operation = { key: string; quantity: number }
type Receipt = { orderId: string; state: string; expiresAt: string }

export default function ReservationControl({ productId, name, changed }: { productId: string; name: string; changed: () => void }) {
  const [quantity, setQuantity] = useState('1')
  const [pending, setPending] = useState(false)
  const [uncertain, setUncertain] = useState<Operation | null>(null)
  const [message, setMessage] = useState('')
  const [receipt, setReceipt] = useState<Receipt | null>(null)
  const busy = useRef(false)

  async function reserve() {
    if (busy.current) return
    const count = Number(quantity)
    if (!Number.isSafeInteger(count) || count < 1 || count > 100) {
      setMessage('Enter a positive whole quantity up to 100.')
      return
    }
    const operation = uncertain ?? { key: crypto.randomUUID(), quantity: count }
    busy.current = true
    setPending(true)
    setReceipt(null)
    setMessage('')
    try {
      const response = await secureFetch('/api/reservations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'Idempotency-Key': operation.key },
        body: JSON.stringify({ productId, quantity: operation.quantity }),
      })
      if (response.status >= 500) throw new Error('Server response is uncertain.')
      const body = await response.json()
      if (!response.ok) {
        setUncertain(null)
        setMessage(typeof body.error === 'string' ? body.error : `Reservation rejected (HTTP ${response.status}).`)
        return
      }
      if (typeof body.orderId !== 'string' || typeof body.state !== 'string' || typeof body.expiresAt !== 'string') {
        throw new Error('Unexpected response.')
      }
      setUncertain(null)
      setReceipt(body)
      setMessage('Reservation confirmed.')
      changed()
    } catch {
      setUncertain(operation)
      setMessage('Unable to confirm the result. Retry safely with the same request. Keep this page open until resolved.')
    } finally {
      busy.current = false
      setPending(false)
    }
  }

  return <div className="reservation-control">
    <div className="reservation-inputs">
      <input aria-label={`Quantity for ${name}`} type="number" min="1" max="100" step="1" value={quantity}
        disabled={pending || uncertain !== null} onChange={event => setQuantity(event.target.value)} />
      <button className="refresh-button" disabled={pending} onClick={() => void reserve()}>
        {pending ? 'Reserving…' : uncertain ? 'Retry reservation' : 'Reserve'}
      </button>
    </div>
    <div role="status" aria-live="polite">
      {message}
      {receipt && <div>Order: {receipt.orderId}<br />{receipt.state} · Expires {new Date(receipt.expiresAt).toLocaleString()}</div>}
    </div>
  </div>
}
