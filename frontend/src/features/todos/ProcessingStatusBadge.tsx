import { Badge } from '@/components/ui/badge'
import { Spinner } from '@/components/ui/spinner'
import type { ProcessingStatus } from '@/types/api'

const labels: Record<ProcessingStatus, string> = {
  QUEUED: 'Queued',
  PROCESSING: 'Processing',
  WAITING_RELEASE: 'Waiting for release',
  PERSISTING: 'Persisting',
  COMPLETED: 'Completed',
  FAILED: 'Failed',
}

const variants: Record<ProcessingStatus, 'default' | 'secondary' | 'outline' | 'destructive'> = {
  QUEUED: 'outline',
  PROCESSING: 'default',
  WAITING_RELEASE: 'secondary',
  PERSISTING: 'default',
  COMPLETED: 'secondary',
  FAILED: 'destructive',
}

/** States in which the backend is still working on the todo (the UI keeps polling while any todo is active). */
export const ACTIVE_STATUSES: readonly ProcessingStatus[] = ['QUEUED', 'PROCESSING', 'WAITING_RELEASE', 'PERSISTING']

export const isActive = (status: ProcessingStatus) => ACTIVE_STATUSES.includes(status)

export const statusLabel = (status: ProcessingStatus) => labels[status] ?? status

export function ProcessingStatusBadge({ status }: { status: ProcessingStatus }) {
  const working = status === 'PROCESSING' || status === 'PERSISTING'
  return (
    <Badge variant={variants[status] ?? 'outline'} data-status={status}>
      {working && <Spinner className="size-3" />}
      {statusLabel(status)}
    </Badge>
  )
}
