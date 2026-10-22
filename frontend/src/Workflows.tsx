import { useEffect, useRef, useState } from 'react'
import { mutate } from './api'
import type { Operation, Order, Product } from './api'

export function OrderActions({ order, changed }: { order: Order; changed: () => void }) {
  const [pending, setPending] = useState(false)
  const [retry, setRetry] = useState<Operation | null>(null)
  const [message, setMessage] = useState('')
  const busy = useRef(false)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer) }, [])
  const expired = Date.parse(order.expiresAt) <= now
  const actions = order.state === 'RESERVED' && !expired ? ['cancel', 'pick', 'shortage'] : order.state === 'PICKED' ? ['fulfill'] : order.state === 'SHORTAGE_PENDING' ? ['resolve-shortage'] : []
  async function submit(action: string) {
    if (busy.current) return
    const op = retry ?? { key: crypto.randomUUID(), url: action === 'cancel' ? `/api/reservations/${order.id}/cancel` : `/api/orders/${order.id}/${action}` }
    busy.current = true; setPending(true)
    try {
      const result = await mutate(op)
      setRetry(null)
      setMessage(result.ok ? 'Order updated.' : result.body.error ?? `Request rejected (${result.status}).`)
      changed()
    } catch {
      setRetry(op); setMessage('Result uncertain. Retry the same action before continuing.')
    } finally { busy.current = false; setPending(false) }
  }
  return <><div className="action-row">{retry ? <button disabled={pending} onClick={() => void submit('')}>Retry action</button> : actions.map(action => <button disabled={pending} key={action} onClick={() => void submit(action)}>{action.replaceAll('-', ' ')}</button>)}</div><div role="status">{message}</div></>
}

export function PosForm({ products, changed }: { products: Product[]; changed: () => void }) {
  const [productId, setProductId] = useState('')
  const [quantity, setQuantity] = useState('1')
  const [retry, setRetry] = useState<Operation | null>(null)
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState('')
  const busy = useRef(false)
  async function submit() {
    if (busy.current) return
    const count = Number(quantity)
    if (!retry && (!productId || !Number.isInteger(count) || count < 1 || count > 100)) { setMessage('Choose a product and a whole quantity from 1 to 100.'); return }
    const key = retry?.key ?? crypto.randomUUID()
    const op = retry ?? { key, url: '/api/pos/sales', body: { eventId: key, productId, quantity: count } }
    busy.current = true; setPending(true)
    try {
      const result = await mutate(op)
      setRetry(null)
      setMessage(result.ok ? `Sale recorded. Receipt/event: ${key}` : result.body.error ?? `Sale rejected (${result.status}).`)
      if (result.ok) changed()
    } catch { setRetry(op); setMessage('Result uncertain. Retry safely using the same sale event.') }
    finally { busy.current = false; setPending(false) }
  }
  return <section className="table-section pos-form"><h2>Record a store sale</h2><p>Sales consume shelf stock and respect active reservations.</p><label>Product<select disabled={pending || !!retry} value={productId} onChange={e => setProductId(e.target.value)}><option value="">Choose a product</option>{products.map(p => <option value={p.id} key={p.id}>{p.sku} — {p.name}</option>)}</select></label><label>Quantity<input type="number" min="1" max="100" step="1" value={quantity} disabled={pending || !!retry} onChange={e => setQuantity(e.target.value)} /></label><button disabled={pending} onClick={() => void submit()}>{pending ? 'Recording…' : retry ? 'Retry sale' : 'Record sale'}</button><p role="status">{message}</p></section>
}
