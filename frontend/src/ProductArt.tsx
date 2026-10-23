import type { Product } from './api'
import { category } from './productCategory'
export function ProductArt({ product }: { product: Product }) {
  const group = category(product)
  const color = group === 'Pantry' ? '#eab760' : group === 'Dairy & eggs' ? '#7aa6a2' : group === 'Bakery' ? '#bb8863' : group === 'Produce' ? '#9ebd73' : '#9eadd0'
  const label = product.name.replace(/\d.*$/, '').split(' ').slice(-3).join(' ')
  return <svg viewBox="0 0 220 210" role="img" aria-label={`${product.name} illustrated packaging`}>
    <ellipse cx="110" cy="186" rx="64" ry="10" fill="#152c1c" opacity=".07" />
    <path d="M62 30 Q110 23 158 30 L166 176 Q110 194 54 176Z" fill={color} />
    <path d="M62 30 Q110 23 158 30 L158 44 Q110 36 62 44Z" fill="#fff" opacity=".35" />
    <path d="M61 79 Q110 65 161 79 L164 142 Q110 156 57 142Z" fill="#fffaf0" />
    <text x="110" y="98" textAnchor="middle" fill="#244738" fontSize="11" fontWeight="700" letterSpacing="2">SHELF SELECT</text>
    <circle cx="110" cy="122" r="14" fill={color} opacity=".55" />
    <path d="M104 125 Q113 108 123 115 Q122 129 108 129" fill="#35654a" />
    <text x="110" y="165" textAnchor="middle" fill="#244738" fontSize="9">{label.slice(0,25)}</text>
  </svg>
}
