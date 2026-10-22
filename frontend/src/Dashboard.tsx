import { secureFetch } from './session'
import { useEffect, useState } from 'react'
import { Package, Database, ShoppingCart, RefreshCw } from 'lucide-react'
import ReservationControl from './ReservationControl'
import { OrderActions, PosForm } from './Workflows'
import type { Product, Order } from './api'
import './App.css'

export default function App() {
  const [page, setPage] = useState('Inventory')
  const [products, setProducts] = useState<Product[]>([])
  const [orders, setOrders] = useState<Order[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)
  const refresh = () => setRevision(v => v + 1)
  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      setLoading(true); setError('')
      try {
        const [catalog, recent] = await Promise.all(['/api/products', '/api/orders'].map(async url => {
          const response = await secureFetch(url, { signal: controller.signal })
          if (!response.ok) throw new Error(`Unable to load data (HTTP ${response.status}).`)
          const data: unknown = await response.json()
          if (!Array.isArray(data)) throw new Error('Unexpected API response.')
          return data
        }))
        if (!controller.signal.aborted) { setProducts(catalog as Product[]); setOrders(recent as Order[]) }
      } catch (err) { if (!controller.signal.aborted) setError(err instanceof Error ? err.message : 'Unable to load data.') }
      finally { if (!controller.signal.aborted) setLoading(false) }
    }
    void load()
    return () => controller.abort()
  }, [revision])
  const filtered = products.filter(p => `${p.name} ${p.sku}`.toLowerCase().includes(search.toLowerCase()))
  return <div className="app-shell"><aside className="sidebar"><div className="brand"><Package />ShelfSync</div><div className="sidebar-label">WORKSPACE</div><nav className="navigation">{['Inventory', 'Orders', 'POS Sales'].map(label => <button className={`nav-item ${page === label ? 'active' : ''}`} aria-current={page === label ? 'page' : undefined} key={label} onClick={() => setPage(label)}>{label === 'Inventory' ? <Database size={18} /> : <ShoppingCart size={18} />}{label}</button>)}</nav><div className="sidebar-footer">Local portfolio demonstration</div></aside>
    <main className="main-content"><header className="topbar"><div><div className="eyebrow">WORKSPACE</div><h1>{page}</h1><p>Reserve stock, fulfill orders, and record store sales.</p></div><button className="refresh-button" disabled={loading} onClick={refresh}><RefreshCw size={16} />Refresh</button></header>
    <section className="stats-grid"><div className="stat-card"><span>Catalog products</span><strong>{products.length}</strong></div><div className="stat-card"><span>Recent active orders</span><strong>{orders.filter(o => ['RESERVED', 'PICKED', 'SHORTAGE_PENDING'].includes(o.state)).length}</strong><small>From the latest 100 orders</small></div><div className="stat-card"><span>Connection</span><strong>{loading ? 'Loading' : error ? 'Error' : 'Online'}</strong></div></section>
    {error && <div className="error-message" role="alert">{error} Use Refresh to try again.</div>}
    {loading && <p role="status">Refreshing data…</p>}
    <div hidden={page !== 'Inventory'}><section className="table-section"><div className="section-heading"><h2>Product catalog</h2><div className="search-box"><input aria-label="Search products" placeholder="Search products or SKUs…" value={search} onChange={e => setSearch(e.target.value)} /></div></div><div className="table-wrapper"><table><thead><tr>{['SKU', 'Product', 'Price', 'Shelf', 'Picked', 'Buffer', 'Status', 'Reservation'].map(h => <th key={h}>{h}</th>)}</tr></thead><tbody>{filtered.map(p => <tr key={p.id}><td className="sku">{p.sku}</td><td>{p.name}</td><td>{new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(p.priceCents / 100)}</td><td>{p.shelfUnits}</td><td>{p.pickedUnits}</td><td>{p.safetyBuffer}</td><td>{p.quarantined ? 'Quarantined' : 'Active'}</td><td><ReservationControl productId={p.id} name={p.name} changed={refresh} /></td></tr>)}</tbody></table>{!loading && !filtered.length && <p className="empty-state">No products found.</p>}</div></section></div>
    <div hidden={page !== 'Orders'}><section className="table-section"><div className="section-heading"><h2>Latest 100 orders</h2></div><div className="table-wrapper"><table><thead><tr>{['Order', 'Product', 'Quantity', 'State', 'Expires', 'Reason', 'Actions'].map(h => <th key={h}>{h}</th>)}</tr></thead><tbody>{orders.map(o => <tr key={o.id}><td className="product-id">{o.id}</td><td>{o.sku} · {o.name}</td><td>{o.quantity}</td><td>{o.state}</td><td>{new Date(o.expiresAt).toLocaleString()}</td><td>{o.reason ?? '—'}</td><td><OrderActions order={o} changed={refresh} /></td></tr>)}</tbody></table>{!loading && !orders.length && <p className="empty-state">No orders yet. Reserve a product to begin.</p>}</div></section></div>
    <div hidden={page !== 'POS Sales'}><PosForm products={products} changed={refresh} /></div>
    </main></div>
}
