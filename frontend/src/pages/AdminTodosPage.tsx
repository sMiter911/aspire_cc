import { EyeIcon, RotateCcwIcon, SendIcon, Trash2Icon } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { FormAlert } from '@/components/FormAlert'
import { RequirePermission } from '@/components/RequireAuthorization'
import { Button, buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { Spinner } from '@/components/ui/spinner'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { DeleteTodoDialog } from '@/features/todos/DeleteTodoDialog'
import { ProcessingStatusBadge, isActive } from '@/features/todos/ProcessingStatusBadge'
import { useInterval } from '@/hooks/useInterval'
import { useQuery } from '@/hooks/useQuery'
import { ApiError } from '@/lib/api'
import * as adminService from '@/services/admin.service'
import { todoService } from '@/services/todo.service'
import type { AdminTodo, ProcessingStatus } from '@/types/api'

const selectClass =
  'h-8 rounded-lg border border-input bg-background px-2.5 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50'

const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '-')

/**
 * All users' todos with processing metadata. The route is ADMIN-only in the UI and the Release/Retry buttons sit
 * behind <RequirePermission>, but that is only convenience: the Todo API returns 403 to anyone who is not an ADMIN.
 */
export function AdminTodosPage() {
  const [userId, setUserId] = useState('')
  const [status, setStatus] = useState<'' | ProcessingStatus>('')

  const users = useQuery(() => adminService.listUsers(0, 100))
  const emailById = useMemo(() => new Map(users.data?.content.map((u) => [u.id, u.email]) ?? []), [users.data])

  const { data, error, loading, reload } = useQuery(
    () => todoService.adminList({ userId: userId || undefined, size: 100 }),
    [userId],
  )
  const rows = useMemo(() => (data?.content ?? []).filter((t) => status === '' || t.processingStatus === status), [data, status])

  useInterval(reload, data?.content.some((t) => isActive(t.processingStatus)) ? 3000 : null)

  const waiting = data?.content.filter((t) => t.processingStatus === 'WAITING_RELEASE').length ?? 0

  return (
    <Card>
      <CardHeader>
        <CardTitle>All todos</CardTitle>
        <CardDescription>Every user&rsquo;s todos and their background processing. Visible to administrators only.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <div className="flex flex-wrap items-end justify-between gap-3">
          <div className="flex flex-wrap gap-3">
            <label className="grid gap-1 text-xs text-muted-foreground">
              Owner
              <select className={selectClass} value={userId} onChange={(e) => setUserId(e.target.value)}>
                <option value="">All users</option>
                {users.data?.content.map((u) => (
                  <option key={u.id} value={u.id}>
                    {u.email}
                  </option>
                ))}
              </select>
            </label>
            <label className="grid gap-1 text-xs text-muted-foreground">
              Processing status
              <select className={selectClass} value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
                <option value="">Any</option>
                <option value="QUEUED">Queued</option>
                <option value="PROCESSING">Processing</option>
                <option value="WAITING_RELEASE">Waiting for release</option>
                <option value="PERSISTING">Persisting</option>
                <option value="COMPLETED">Completed</option>
                <option value="FAILED">Failed</option>
              </select>
            </label>
          </div>
          <RequirePermission permission="todos:release">
            <ReleaseAllDialog waiting={waiting} onDone={reload} />
          </RequirePermission>
        </div>

        {loading && !data && <Skeleton className="h-40 w-full" />}
        {error && <FormAlert title="Could not load todos" message={error} />}
        {data && (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Todo</TableHead>
                <TableHead>User</TableHead>
                <TableHead>Processing status</TableHead>
                <TableHead>Created</TableHead>
                <TableHead>Queued</TableHead>
                <TableHead>Worker</TableHead>
                <TableHead>Last attempt</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.length === 0 && (
                <TableRow>
                  <TableCell colSpan={8} className="text-center text-muted-foreground">
                    No todos match.
                  </TableCell>
                </TableRow>
              )}
              {rows.map((t) => (
                <TableRow key={t.id}>
                  <TableCell className="max-w-48 truncate">{t.title}</TableCell>
                  <TableCell>{emailById.get(t.userId) ?? t.userId}</TableCell>
                  <TableCell>
                    <ProcessingStatusBadge status={t.processingStatus} />
                    {t.processingError && <div className="mt-1 text-xs text-destructive">{t.processingError}</div>}
                  </TableCell>
                  <TableCell>{fmt(t.createdAt)}</TableCell>
                  <TableCell>{fmt(t.queuedAt)}</TableCell>
                  <TableCell>{t.workerId ?? '-'}</TableCell>
                  <TableCell>{fmt(t.lastAttemptAt)}</TableCell>
                  <TableCell className="space-x-1 whitespace-nowrap text-right">
                    <Link
                      to={`/todos/${t.id}`}
                      className={buttonVariants({ variant: 'ghost', size: 'icon' })}
                      aria-label={`View ${t.title}`}
                    >
                      <EyeIcon />
                    </Link>
                    <RowActions todo={t} onDone={reload} />
                    <DeleteTodoDialog
                      todo={t}
                      onDeleted={reload}
                      trigger={
                        <Button variant="ghost" size="icon" aria-label={`Delete ${t.title}`}>
                          <Trash2Icon />
                        </Button>
                      }
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
        {data && <p className="text-xs text-muted-foreground">{data.totalElements} todos</p>}
      </CardContent>
    </Card>
  )
}

/** Release (WAITING_RELEASE) and Retry (FAILED) are only offered where they make sense and only to admins. */
function RowActions({ todo, onDone }: { todo: AdminTodo; onDone: () => void }) {
  const [busy, setBusy] = useState(false)

  const run = async (action: () => Promise<unknown>, ok: string) => {
    setBusy(true)
    try {
      await action()
      toast.success(ok)
      onDone()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'The action failed')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      {todo.processingStatus === 'WAITING_RELEASE' && (
        <RequirePermission permission="todos:release">
          <Button
            variant="outline"
            size="sm"
            disabled={busy}
            onClick={() => run(() => todoService.release(todo.id), 'Release requested')}
          >
            <SendIcon /> Release
          </Button>
        </RequirePermission>
      )}
      {todo.processingStatus === 'FAILED' && (
        <RequirePermission permission="todos:retry">
          <Button
            variant="outline"
            size="sm"
            disabled={busy}
            onClick={() => run(() => todoService.retry(todo.id), 'Todo re-queued')}
          >
            <RotateCcwIcon /> Retry
          </Button>
        </RequirePermission>
      )}
    </>
  )
}

function ReleaseAllDialog({ waiting, onDone }: { waiting: number; onDone: () => void }) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)

  const confirm = async () => {
    setBusy(true)
    try {
      const { waiting: n } = await todoService.releaseAll()
      toast.success(n === 0 ? 'Nothing was waiting' : `Release requested for ${n} todo(s)`)
      setOpen(false)
      onDone()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'The action failed')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger
        render={
          <Button variant="outline" disabled={waiting === 0}>
            <SendIcon /> Release all waiting ({waiting})
          </Button>
        }
      />
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Release all waiting todos?</DialogTitle>
          <DialogDescription>
            The worker will finish and persist every todo that is currently waiting for release. Otherwise they are
            released automatically on the worker&rsquo;s schedule.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button onClick={confirm} disabled={busy}>
            {busy && <Spinner />}
            Release
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
