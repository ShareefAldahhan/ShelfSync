import { useEffect, useState } from 'react'
import { Printer, CheckCircle2 } from 'lucide-react'
import { fetchPaidReceipt, money } from './checkout'
import type { Receipt } from './checkout'

type ReceiptProps = { id: string; autoShow?: boolean }

export default function OrderReceipt({ id, autoShow = false }: ReceiptProps) {
  return <ReceiptContent key={`${id}:${autoShow}`} id={id} autoShow={autoShow} />
}

function ReceiptContent({ id, autoShow }: ReceiptProps) {
  const [receipt, setReceipt] = useState<Receipt | null>(null)
  const [error, setError] = useState('')
  const [pending, setPending] = useState(autoShow)
  const [request, setRequest] = useState(autoShow ? 1 : 0)

  useEffect(() => {
    if (!request) return
    const controller = new AbortController()

    async function loadReceipt() {
      try {
        const result = await fetchPaidReceipt(id, controller.signal)
        if (!controller.signal.aborted) {
          setReceipt(result)
          setError('')
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setError(error instanceof Error ? error.message : 'Unable to load receipt.')
        }
      } finally {
        if (!controller.signal.aborted) setPending(false)
      }
    }

    void loadReceipt()
    return () => controller.abort()
  }, [id, request])

  function load() {
    setPending(true)
    setError('')
    setRequest(value => value + 1)
  }

  function printReceipt(button: HTMLButtonElement) {
    document.querySelectorAll('.receipt-panel').forEach(panel => {
      panel.removeAttribute('data-print-target')
    })
    button.closest('.receipt-panel')?.setAttribute('data-print-target', 'true')
    window.print()
  }
  const items = receipt?.checkoutItems ?? (receipt ? [{
    orderId: receipt.id,
    name: receipt.name,
    sku: receipt.sku,
    quantity: receipt.quantity,
    unitPriceCents: receipt.unitPriceCents,
    totalCents: receipt.totalCents,
  }] : [])

  return <>
    {!receipt && <button className="receipt-open" disabled={pending} onClick={load}>
      {pending ? 'Preparing receipt\u2026' : 'View receipt'}
    </button>}
    {error && <p role="alert">{error}</p>}
    {receipt && <article className="receipt-panel paid-receipt">
      <div className="receipt-brand">
        <strong>ShelfSync Market</strong><span>PAYMENT RECEIPT</span>
      </div>
      <div className="receipt-confirmed"><CheckCircle2 size={20}/>Test payment confirmed</div>
      <dl className="receipt-reference">
        <div><dt>Order</dt><dd>{receipt.id}</dd></div>
        <div><dt>Ordered</dt><dd>{new Date(receipt.createdAt).toLocaleString()}</dd></div>
        {receipt.cartId && <div><dt>Pickup group</dt><dd>{receipt.cartId}</dd></div>}
      </dl>
      {items.map(item => <div className="receipt-line" key={item.orderId}>
        <div>
          <strong>{item.name}</strong>
          <small>{item.sku} · {item.quantity} × {money(item.unitPriceCents)}</small>
        </div>
        <strong>{money(item.totalCents)}</strong>
      </div>)}
      <div className="receipt-total">
        <span>Total paid · USD</span>
        <strong>{money(receipt.checkoutTotalCents ?? receipt.totalCents)}</strong>
      </div>
      <p className="receipt-footnote">
        Stripe test payment · No real money collected.<br/>
        Pickup status: {receipt.state.replaceAll('_', ' ')}
        {receipt.returnedQuantity > 0 && ` · Returned units: ${receipt.returnedQuantity}`}
      </p>
      <div className="receipt-actions">
        <button className="payment-primary" onClick={e => printReceipt(e.currentTarget)}>
          <Printer size={16}/>Print receipt
        </button>
        {!autoShow && <button className="payment-secondary" onClick={() => setReceipt(null)}>Close</button>}
      </div>
    </article>}
  </>
}
