import { secureFetch } from './session'
import { useEffect, useState } from 'react'
import { ShoppingBag, Search, ArrowUpRight, MapPin, RefreshCw, Leaf } from 'lucide-react'
import { ProductArt } from './ProductArt'
import { category } from './productCategory'
import ReservationControl from './ReservationControl'
import { OrderActions, PosForm } from './Workflows'
import type { Product, Order } from './api'
import './App.css'

export default function App() {
  const [page, setPage] = useState('Inventory')
  const [products, setProducts] = useState<Product[]>([])
  const [orders, setOrders] = useState<Order[]>([])
  const [search, setSearch] = useState('')
  const [department, setDepartment] = useState('All products')
  const [sort, setSort] = useState('featured')
  const [orderSearch, setOrderSearch] = useState('')
  const [stateFilter, setStateFilter] = useState('ALL')
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer) }, [])
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
  const filtered = products.filter(p => `${p.name} ${p.sku}`.toLowerCase().includes(search.toLowerCase()) && (department === 'All products' || category(p) === department)).sort((a,b) => sort === 'price-low' ? a.priceCents-b.priceCents : sort === 'price-high' ? b.priceCents-a.priceCents : a.sku.localeCompare(b.sku))
  const effectiveState = (o: Order) => o.state === 'RESERVED' && Date.parse(o.expiresAt) <= now ? 'EXPIRED' : o.state
  const visibleOrders = orders.filter(o => (stateFilter === 'ALL' || effectiveState(o) === stateFilter) && `${o.id} ${o.sku} ${o.name}`.toLowerCase().includes(orderSearch.toLowerCase()))
  return <div className="storefront">
    <div className="announcement">A little local. A lot to love. <span>Reserve your everyday essentials for pickup.</span></div>
    <header className="store-header"><button className="store-logo" onClick={() => setPage('Inventory')}><ShoppingBag size={28} />ShelfSync<span>MARKET</span></button><div className="store-search"><Search size={18} /><input aria-label="Search products" placeholder="Find your everyday favorites…" value={search} onChange={e => { setSearch(e.target.value); setPage('Inventory') }} /></div><div className="store-location"><MapPin size={18} /><span>Your neighborhood store<small>Local pickup</small></span></div><button className="bag-button" onClick={() => setPage('Orders')}><ShoppingBag size={20} /><span>Reservations</span><b>{orders.filter(o => ['RESERVED','PICKED','SHORTAGE_PENDING'].includes(effectiveState(o))).length}</b></button></header>
    <nav className="store-nav" aria-label="Store navigation"><div>{['Inventory','Orders','POS Sales'].map(label => <button key={label} aria-current={page === label ? 'page' : undefined} className={page === label ? 'selected' : ''} onClick={() => setPage(label)}>{label === 'Inventory' ? 'Shop all' : label === 'Orders' ? 'Reservations' : 'Store checkout'}</button>)}</div><span><Leaf size={14} /> Thoughtfully stocked. Simply shopped.</span></nav>
    <main className="shop-main">
    {page === 'Inventory' && <><section className="shop-hero"><div className="hero-copy"><div className="hero-eyebrow">THE EVERYDAY, DONE WELL</div><h1>Your favorites.<br />Just around<br /><em>the corner.</em></h1><p>From pantry staples to little daily essentials.<br />Find what you need and reserve it for pickup.</p><a href="#shop-products">Explore the shelves <ArrowUpRight size={18} /></a></div><div className="hero-art" aria-hidden="true"><div className="hero-orbit" />{products.slice(0,3).map((p,i) => <div className={`hero-package package-${i}`} key={p.id}><ProductArt product={p} /></div>)}<div className="hero-stamp">YOUR DAILY<br /><strong>GOOD STUFF</strong><br />ALL IN ONE PLACE</div></div></section><div className="service-strip"><span><b>01</b> Pick your favorites</span><span><b>02</b> Reserve in a few clicks</span><span><b>03</b> Get ready for pickup</span><small>Reservations held for 15 minutes</small></div></>}
    {page !== 'Inventory' && <div className="shop-section-heading"><h1>{page === 'Orders' ? 'Reservations' : 'Store checkout'}</h1></div>}
    {error && <div className="error-message" role="alert">{error} <button onClick={refresh}>Try again</button></div>}
    <div hidden={page !== 'Inventory'} id="shop-products"><div className="shop-section-heading"><div className="eyebrow">A WELL-STOCKED EVERYDAY</div><h2>Meet your daily essentials<span>({filtered.length})</span></h2><p>A few good things for your pantry, your home, and everything in between.</p></div><div className="shop-toolbar"><div className="category-tabs">{['All products','Pantry','Dairy & eggs','Produce','Bakery','Home & care'].map(c => <button key={c} aria-pressed={department === c} className={department === c ? 'selected' : ''} onClick={() => setDepartment(c)}>{c}</button>)}</div><label className="sort-label">Sort by <select aria-label="Sort products" value={sort} onChange={e => setSort(e.target.value)}><option value="featured">Featured</option><option value="price-low">Price: low to high</option><option value="price-high">Price: high to low</option></select></label></div>
    {loading && <p role="status" className="loading-note">Refreshing the shelves…</p>}
    <div className="product-grid">{filtered.map(p => <article className="product-card" key={p.id}><div className={`product-visual visual-${category(p).split(' ')[0].toLowerCase()}`}><span className="product-category">{category(p)}</span><ProductArt product={p} /></div><div className="product-info"><small>{p.sku} · {category(p)}</small><h3>{p.name}</h3><div className="product-price">{new Intl.NumberFormat('en-US', { style:'currency',currency:'USD' }).format(p.priceCents / 100)}<span> / each</span></div><p className="stock-label"><i />{p.quarantined ? 'Temporarily unavailable' : p.shelfUnits === 0 ? 'Out of shelf stock' : 'Check availability when reserving'}</p><ReservationControl productId={p.id} name={p.name} changed={refresh} unavailable={p.quarantined || p.shelfUnits === 0} /></div></article>)}</div>{!loading && !filtered.length && <div className="empty-state">No products found. Try another search or department.</div>}<div className="shop-bottom"><span>{filtered.length} everyday essentials · Prices in USD</span><button onClick={refresh} disabled={loading}><RefreshCw size={14} />Refresh availability</button></div></div>
    <div hidden={page !== 'Orders'}><section className="table-section"><div className="section-heading"><h2>Latest 100 orders</h2><div className="order-filters"><input aria-label="Search orders" placeholder="Order ID, SKU or product…" value={orderSearch} onChange={e => setOrderSearch(e.target.value)} /><select aria-label="Filter orders by state" value={stateFilter} onChange={e => setStateFilter(e.target.value)}>{["ALL", "RESERVED", "PICKED", "SHORTAGE_PENDING", "RESTOCK_PENDING", "CANCELLED", "EXPIRED", "FULFILLED"].map(state => <option key={state} value={state}>{state.replaceAll("_", " ")}</option>)}</select></div></div><div className="table-wrapper"><table><thead><tr>{['Order', 'Product', 'Quantity', 'State', 'Expires', 'Reason', 'Actions'].map(h => <th key={h}>{h}</th>)}</tr></thead><tbody>{visibleOrders.map(o => <tr key={o.id}><td className="product-id">{o.id}</td><td>{o.sku} · {o.name}</td><td>{o.quantity}</td><td><span className="state-badge">{effectiveState(o).replaceAll("_", " ")}</span>{effectiveState(o) !== o.state && <small> Awaiting expiry sweep</small>}</td><td>{new Date(o.expiresAt).toLocaleString()}</td><td>{o.reason ?? '—'}</td><td><OrderActions order={o} changed={refresh} /></td></tr>)}</tbody></table>{!loading && !visibleOrders.length && <p className="empty-state">No matching orders. Try another filter or reserve a product.</p>}</div></section></div>
    <div hidden={page !== 'POS Sales'}><PosForm products={products} changed={refresh} /></div>
    </main><footer className="store-footer"><strong>ShelfSync<span> Market</span></strong><p>Good things, close to home.</p><small>A fictional local store · Product illustrations are representative · Prices shown in USD</small></footer></div>
}
