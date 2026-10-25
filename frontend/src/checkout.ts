import { secureFetch } from './session'

export type CheckoutItem = {
  orderId: string
  name: string
  sku: string
  quantity: number
  unitPriceCents: number
  totalCents: number
}

export type Receipt = {
  id: string
  cartId: string | null
  sku: string
  name: string
  quantity: number
  unitPriceCents: number
  totalCents: number
  state: string
  paymentState: string | null
  createdAt: string
  returnedQuantity: number
  checkoutTotalCents?: number
  checkoutItems?: CheckoutItem[]
}

const usdFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
})

export function money(cents: number): string {
  return usdFormatter.format(cents / 100)
}

export function redirectToCheckout(destination: string): void {
  const url = new URL(destination)
  if (url.protocol !== 'https:' || url.hostname !== 'checkout.stripe.com' ||
      url.username || url.password || url.port) {
    throw new Error('Invalid checkout destination.')
  }
  window.location.assign(url.href)
}

export async function fetchPaidReceipt(id: string, signal: AbortSignal): Promise<Receipt> {
  const response = await secureFetch(`/api/orders/${id}/receipt`, { signal })
  if (!response.ok) throw new Error('Unable to load receipt.')
  const receipt: Receipt = await response.json()
  if (receipt.paymentState !== 'PAID') {
    throw new Error('Your receipt will be available after payment is confirmed.')
  }
  return receipt
}
