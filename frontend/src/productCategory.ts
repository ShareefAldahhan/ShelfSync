import type { Product } from './api'
export function category(product: Product) {
  if (/chips|beans|cereal/i.test(product.name)) return 'Pantry'
  if (/milk|eggs/i.test(product.name)) return 'Dairy & eggs'
  if (/bread/i.test(product.name)) return 'Bakery'
  if (/apples/i.test(product.name)) return 'Produce'
  return 'Home & care'
}
