import ItemEditor from './ItemEditor'
import { useEffect, useRef, useState } from 'react'
import JsBarcode from 'jsbarcode'
import { ArrowLeft, MapPin, RefreshCw, Package, ShieldCheck, Activity } from 'lucide-react'
import { secureFetch } from './session'
import { ProductArt } from './ProductArt'
import ReservationControl from './ReservationControl'
import type { Product } from './api'
import './ProductDetails.css'

type Profile = { barcode: string | null; aisle: string | null; bay: string | null; shelf: string | null; bin: string | null; brand: string | null; supplier: string | null; casePack: number | null; reorderPoint: number | null; notes: string | null }
type Details = { id: string; sku: string; name: string; priceCents: number; profile: Profile; stock: { shelf: number; picked: number; reserved: number; safetyBuffer: number; available: number; quarantined: boolean; quarantineReason: string | null; updatedAt: string }; posUnits30Days: number; fulfilledUnits30Days: number; recentSales: { eventId: string; quantity: number; committedAt: string }[]; activity: { id: number; kind: string; shelfDelta: number; pickedDelta: number; orderId: string | null; posEventId: string | null; createdAt: string; reason: string | null; actor: string | null }[]; asOf: string }
function Barcode({ value }: { value: string }) {
  const target = useRef<SVGSVGElement>(null)
  useEffect(() => { if (target.current) { try { JsBarcode(target.current, value, { format:'CODE128',height:55,width:2,margin:12,fontSize:15 }) } catch { target.current.textContent='Barcode cannot be rendered' } } }, [value])
  return <svg ref={target} role="img" aria-label={`Code 128 barcode ${value}`} />
}
const date = (value: string) => new Date(value).toLocaleString()
const signed = (value: number) => value > 0 ? `+${value}` : String(value)

export default function ProductDetails({ id, back, changed }: { id: string; back: () => void; changed: () => void }) {
  const [details,setDetails] = useState<Details | null>(null)
  const [error,setError] = useState('')
  const [loading,setLoading] = useState(true)
  const [revision,setRevision] = useState(0)
  const [tab,setTab] = useState('Overview')
  useEffect(() => { window.scrollTo(0,0) }, [id])
  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      setLoading(true);setError('')
      try {
        const response=await secureFetch(`/api/products/${id}/details`,{signal:controller.signal})
        if (!response.ok) throw new Error(response.status===404 ? 'This product was not found.' : `Product details unavailable (HTTP ${response.status}).`)
        const body=await response.json() as Details
        if (!controller.signal.aborted) setDetails(body)
      } catch(err) { if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Unable to load product.') }
      finally { if (!controller.signal.aborted) setLoading(false) }
    }
    void load();return () => controller.abort()
  },[id,revision])
  const refresh=()=>setRevision(v=>v+1)
  const product=details ? { id:details.id,sku:details.sku,name:details.name,priceCents:details.priceCents,shelfUnits:details.stock.shelf,pickedUnits:details.stock.picked,safetyBuffer:details.stock.safetyBuffer,quarantined:details.stock.quarantined } satisfies Product : null
  return <div className="associate-page"><header className="associate-top"><button onClick={back}><ArrowLeft size={17} />Back to shop</button><strong>ShelfSync <span>Associate workspace</span></strong><button onClick={refresh} disabled={loading}><RefreshCw size={15} />Refresh</button></header><main className="associate-main">
    {error && <div className="error-message" role="alert">{error} <button onClick={refresh}>Try again</button></div>}
    {loading && <p role="status">Loading current item information…</p>}
    {details && product && <><div className="item-breadcrumb">Store / Item lookup / {details.sku}</div><section className="item-heading"><div className="item-art"><ProductArt product={product} /></div><div><div className="hero-eyebrow">ITEM DETAILS · ASSOCIATE VIEW</div><h1>{details.name}</h1><div className="item-identifiers"><span>SKU {details.sku}</span><span className={details.stock.quarantined ? 'item-alert' : 'item-good'}>{details.stock.quarantined ? 'Quarantined' : details.stock.available > 0 ? 'Available to reserve' : 'No available units'}</span></div><div className="item-price">{new Intl.NumberFormat('en-US',{style:'currency',currency:'USD'}).format(details.priceCents/100)}<small>Current retail price / each</small></div><p className="item-snapshot">Snapshot as of {date(details.asOf)}{loading ? ' · Refreshing' : ''}</p></div><div className="item-barcode"><Barcode value={details.profile.barcode ?? details.sku} /><small>Internal Code 128 · not a manufacturer UPC</small><code>{details.id}</code></div></section>
    {details.stock.quarantined && <div className="error-message"><ShieldCheck size={16} /> Stock is quarantined: {details.stock.quarantineReason ?? 'Reason not recorded'}</div>}
    <div className="item-stock-grid">{[['Total on hand',details.stock.shelf+details.stock.picked],['Shelf units',details.stock.shelf],['Picked units',details.stock.picked],['Active reservations',details.stock.reserved],['Safety buffer',details.stock.safetyBuffer],['Available to reserve',details.stock.available]].map(([label,value])=><div key={label} className="item-stock-card"><span>{label}</span><strong>{value}</strong></div>)}</div><p className="item-explainer">Available = shelf − live reservations − safety buffer, minimum zero. Quarantined items have zero availability. Counts are a snapshot; every action rechecks stock.</p>
    <nav className="item-tabs" aria-label="Item information">{['Overview','Sales history','Inventory activity'].map(label=><button key={label} aria-current={tab===label ? 'page' : undefined} onClick={()=>setTab(label)} className={tab===label ? 'selected' : ''}>{label}</button>)}</nav>
    {tab==='Overview' && <div className="item-overview"><section className="item-panel"><h2><MapPin size={19} />Shelf location</h2><div className="location-grid">{[['Aisle',details.profile.aisle],['Bay',details.profile.bay],['Shelf',details.profile.shelf],['Bin',details.profile.bin]].map(([label,value])=><div key={label}><span>{label}</span><strong>{value ?? 'Not assigned'}</strong></div>)}</div><p>{details.profile.notes ?? 'No item notes recorded.'}</p><ItemEditor key={`${details.asOf}:location`} id={id} location={details.profile} shelfUnits={details.stock.shelf} mode="location" changed={()=>{refresh();changed()}} /></section><section className="item-panel"><h2><Package size={19} />Item information</h2><dl>{[['Brand',details.profile.brand],['Supplier',details.profile.supplier],['Case pack',details.profile.casePack],['Reorder point',details.profile.reorderPoint],['Inventory last updated',date(details.stock.updatedAt)]].map(([label,value])=><div key={String(label)}><dt>{label}</dt><dd>{value ?? 'Not recorded'}</dd></div>)}</dl></section><section className="item-panel"><h2>Stock correction</h2><ItemEditor key={`${details.asOf}:stock`} id={id} location={details.profile} shelfUnits={details.stock.shelf} mode="stock-correction" changed={()=>{refresh();changed()}} /><h2>Reserve this item</h2><p>Create a 15-minute reservation. Picking and fulfillment remain in Reservations.</p><ReservationControl productId={id} name={details.name} unavailable={details.stock.quarantined || details.stock.available===0} changed={()=>{refresh();changed()}} /></section><section className="item-panel"><h2><Activity size={19} />Last 30 days</h2><div className="sales-stats"><div><strong>{details.posUnits30Days}</strong><span>POS units sold</span></div><div><strong>{details.fulfilledUnits30Days}</strong><span>Online units fulfilled</span></div></div><p>Recorded units only. Historical POS prices are not captured, so revenue is not estimated from today's price.</p></section></div>}
    {tab==='Sales history' && <section className="item-panel"><h2>Store sales history</h2><p>Latest 100 committed POS transactions for this item. Online fulfillment is summarized above and recorded in inventory activity.</p><div className="table-wrapper"><table><thead><tr><th>Recorded at</th><th>Quantity sold</th><th>POS event / receipt ID</th></tr></thead><tbody>{details.recentSales.map(s=><tr key={s.eventId}><td>{date(s.committedAt)}</td><td>{s.quantity}</td><td><code>{s.eventId}</code></td></tr>)}</tbody></table></div>{!details.recentSales.length && <p className="empty-state">No POS sales recorded for this item.</p>}</section>}
    {tab==='Inventory activity' && <section className="item-panel"><h2>Inventory activity</h2><p>Latest 100 recorded stock and order events. Zero deltas indicate lifecycle changes without physical movement.</p><div className="table-wrapper"><table><thead><tr><th>When</th><th>Event</th><th>Shelf change</th><th>Picked change</th><th>Reason / operator</th><th>Reference</th></tr></thead><tbody>{details.activity.map(a=><tr key={a.id}><td>{date(a.createdAt)}</td><td><span className="state-badge">{a.kind}</span></td><td>{signed(a.shelfDelta)}</td><td>{signed(a.pickedDelta)}</td><td>{a.reason ?? '—'}{a.actor && <small className="audit-actor">{a.actor}</small>}</td><td><code>{a.orderId ?? a.posEventId ?? `Event ${a.id}`}</code></td></tr>)}</tbody></table></div>{!details.activity.length && <p className="empty-state">No activity recorded for this item.</p>}</section>}</>}
  </main></div>
}
