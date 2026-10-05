import { PencilIcon, PlusIcon, Trash2Icon } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { FormAlert } from '@/components/FormAlert'
import { RequirePermission } from '@/components/RequireAuthorization'
import { Badge } from '@/components/ui/badge'
import { Button, buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Skeleton } from '@/components/ui/skeleton'
import { DeleteTodoDialog } from '@/features/todos/DeleteTodoDialog'
import { ProcessingStatusBadge, isActive } from '@/features/todos/ProcessingStatusBadge'
import { TodoFormDialog } from '@/features/todos/TodoFormDialog'
import { useAuth } from '@/hooks/useAuth'
import { useInterval } from '@/hooks/useInterval'
import { useQuery } from '@/hooks/useQuery'
import { ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { todoService } from '@/services/todo.service'
import type { Todo } from '@/types/api'

const fmtDate = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : null)

type Filter = 'all' | 'open' | 'done'
const filterToCompleted = (f: Filter) => (f === 'all' ? undefined : f === 'done')

export function TodosPage() {
  const [filter, setFilter] = useState<Filter>('all')
  const { data, error, loading, reload } = useQuery(
    () => todoService.list({ completed: filterToCompleted(filter), size: 100 }),
    [filter],
  )

  // Processing happens asynchronously in the background: refresh while any todo is still in flight.
  const inFlight = data?.content.some((t) => isActive(t.processingStatus)) ?? false
  useInterval(reload, inFlight ? 3000 : null)

  const toggle = async (todo: Todo, checked: boolean) => {
    try {
      if (checked) await todoService.complete(todo.id)
      else
        await todoService.update(todo.id, {
          title: todo.title,
          description: todo.description,
          dueDate: todo.dueDate,
          isCompleted: false,
        })
      reload()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not update the todo')
    }
  }

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold">My todos</h1>
          <p className="text-muted-foreground">Only you can see these.</p>
        </div>
        <RequirePermission permission="todos:manage-own">
          <TodoFormDialog
            trigger={
              <Button>
                <PlusIcon /> New todo
              </Button>
            }
            onSaved={reload}
          />
        </RequirePermission>
      </div>

      <div className="flex gap-2" role="group" aria-label="Filter todos">
        {(['all', 'open', 'done'] as const).map((f) => (
          <Button key={f} size="sm" variant={filter === f ? 'default' : 'outline'} onClick={() => setFilter(f)}>
            {f === 'all' ? 'All' : f === 'open' ? 'Open' : 'Done'}
          </Button>
        ))}
      </div>

      {loading && !data && <Skeleton className="h-32 w-full" />}
      {error && <FormAlert title="Could not load todos" message={error} />}
      {data && data.content.length === 0 && (
        <Card>
          <CardContent className="py-8 text-center text-muted-foreground">Nothing here yet.</CardContent>
        </Card>
      )}
      <ul className="grid gap-2">
        {data?.content.map((todo) => (
          <li key={todo.id}>
            <Card size="sm">
              <CardContent className="flex items-center gap-3">
                <Checkbox
                  aria-label={`Mark "${todo.title}" ${todo.isCompleted ? 'not done' : 'done'}`}
                  checked={todo.isCompleted}
                  onCheckedChange={(c) => toggle(todo, c === true)}
                />
                <Link to={`/todos/${todo.id}`} className="min-w-0 flex-1">
                  <div className={cn('truncate font-medium', todo.isCompleted && 'text-muted-foreground line-through')}>
                    {todo.title}
                  </div>
                  {todo.description && <div className="truncate text-sm text-muted-foreground">{todo.description}</div>}
                  {fmtDate(todo.dueDate) && (
                    <div className="text-xs text-muted-foreground">Due {fmtDate(todo.dueDate)}</div>
                  )}
                </Link>
                <ProcessingStatusBadge status={todo.processingStatus} />
                <DeleteTodoDialog
                  todo={todo}
                  onDeleted={reload}
                  trigger={
                    <Button variant="ghost" size="icon" aria-label={`Delete ${todo.title}`}>
                      <Trash2Icon />
                    </Button>
                  }
                />
              </CardContent>
            </Card>
          </li>
        ))}
      </ul>
    </div>
  )
}

export function TodoDetailPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { user } = useAuth()
  const { data: todo, error, errorStatus, loading, reload } = useQuery(() => todoService.get(id), [id])

  useInterval(reload, todo && isActive(todo.processingStatus) ? 3000 : null)

  if (loading && !todo) return <Skeleton className="h-40 max-w-xl" />

  if (errorStatus === 404) {
    // The API answers 404 both for a missing todo and for someone else's: so does the UI.
    return (
      <Card className="max-w-xl">
        <CardHeader>
          <CardTitle>Todo not found</CardTitle>
          <CardDescription>It does not exist, or you do not have access to it.</CardDescription>
        </CardHeader>
        <CardContent>
          <Link to="/todos" className={buttonVariants({ variant: 'outline' })}>
            Back to my todos
          </Link>
        </CardContent>
      </Card>
    )
  }
  if (error || !todo) return <FormAlert title="Could not load todo" message={error ?? 'Unknown error'} />

  const foreign = todo.userId !== user?.id // only an ADMIN can be looking at someone else's todo

  return (
    <Card className="max-w-xl">
      <CardHeader>
        <CardTitle className={cn(todo.isCompleted && 'line-through')}>{todo.title}</CardTitle>
        <CardDescription className="flex flex-wrap items-center gap-2">
          <Badge variant={todo.isCompleted ? 'secondary' : 'default'}>{todo.isCompleted ? 'Done' : 'Open'}</Badge>
          <ProcessingStatusBadge status={todo.processingStatus} />
          {foreign && <Badge variant="outline">Owner {todo.userId}</Badge>}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {todo.processingStatus === 'FAILED' && (
          <FormAlert title="Processing failed" message="Background processing could not complete. An administrator can retry it." />
        )}
        {todo.description && <p className="whitespace-pre-wrap text-sm">{todo.description}</p>}
        <dl className="grid grid-cols-[7rem_1fr] gap-y-1 text-sm">
          <dt className="text-muted-foreground">Due</dt>
          <dd>{fmtDate(todo.dueDate) ?? 'No due date'}</dd>
          <dt className="text-muted-foreground">Processed</dt>
          <dd>{todo.processedAt ? new Date(todo.processedAt).toLocaleString() : 'Not yet'}</dd>
          <dt className="text-muted-foreground">Created</dt>
          <dd>{new Date(todo.createdAt).toLocaleString()}</dd>
          <dt className="text-muted-foreground">Updated</dt>
          <dd>{new Date(todo.updatedAt).toLocaleString()}</dd>
        </dl>
        <div className="flex flex-wrap gap-2">
          <TodoFormDialog
            todo={todo}
            onSaved={reload}
            trigger={
              <Button variant="outline">
                <PencilIcon /> Edit
              </Button>
            }
          />
          <DeleteTodoDialog
            todo={todo}
            onDeleted={() => navigate(foreign ? '/admin/todos' : '/todos', { replace: true })}
            trigger={
              <Button variant="destructive">
                <Trash2Icon /> Delete
              </Button>
            }
          />
          <Link to={foreign ? '/admin/todos' : '/todos'} className={buttonVariants({ variant: 'ghost' })}>
            Back
          </Link>
        </div>
      </CardContent>
    </Card>
  )
}
