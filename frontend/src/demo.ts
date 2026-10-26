let simulatedPayments = false

export async function detectDemo(): Promise<boolean> {
  try {
    const response = await fetch('/__demo/info', { cache: 'no-store' })
    simulatedPayments = response.ok && response.headers.get('content-type')?.includes('application/json') === true &&
      (await response.json()).simulatedPayments === true
  } catch { simulatedPayments = false }
  return simulatedPayments
}

export const demoPaymentsEnabled = () => simulatedPayments
