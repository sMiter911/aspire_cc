import { useState, type ReactElement } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Spinner } from '@/components/ui/spinner'
import { ApiError } from '@/lib/api'
import { todoService } from '@/services/todo.service'
import type { Todo } from '@/types/api'

export function DeleteTodoDialog({
  todo,
  trigger,
  onDeleted,
}: {
  todo: Todo
  trigger: ReactElement
  onDeleted: () => void
}) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)

  const confirm = async () => {
    setBusy(true)
    try {
      await todoService.remove(todo.id)
      toast.success('Todo deleted')
      setOpen(false)
      onDeleted()
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not delete the todo')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={trigger} />
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Delete this todo?</DialogTitle>
          <DialogDescription>
            &ldquo;{todo.title}&rdquo; will be permanently removed. This cannot be undone.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="destructive" onClick={confirm} disabled={busy}>
            {busy && <Spinner />}
            Delete
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
