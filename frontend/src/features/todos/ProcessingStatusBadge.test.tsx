import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { ProcessingStatus } from '@/types/api'
import { ACTIVE_STATUSES, ProcessingStatusBadge, isActive, statusLabel } from './ProcessingStatusBadge'

describe('ProcessingStatusBadge', () => {
  it.each<[ProcessingStatus, string]>([
    ['QUEUED', 'Queued'],
    ['PROCESSING', 'Processing'],
    ['WAITING_RELEASE', 'Waiting for release'],
    ['PERSISTING', 'Persisting'],
    ['COMPLETED', 'Completed'],
    ['FAILED', 'Failed'],
  ])('renders %s as "%s"', (status, label) => {
    render(<ProcessingStatusBadge status={status} />)
    expect(screen.getByText(label)).toHaveAttribute('data-status', status)
    expect(statusLabel(status)).toBe(label)
  })

  it('keeps polling only while the backend is still working on a todo', () => {
    expect(ACTIVE_STATUSES.every(isActive)).toBe(true)
    expect(isActive('COMPLETED')).toBe(false)
    expect(isActive('FAILED')).toBe(false)
  })
})
