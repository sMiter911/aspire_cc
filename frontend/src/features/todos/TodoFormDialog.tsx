import { zodResolver } from '@hookform/resolvers/zod'
import { useState, type ReactElement } from 'react'
import { useForm } from 'react-hook-form'
import { toast } from 'sonner'
import { z } from 'zod'
import { FormAlert } from '@/components/FormAlert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from '@/components/ui/dialog'
import { Field, FieldError, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/lib/api'
import { todoService } from '@/services/todo.service'
import type { Todo } from '@/types/api'

const schema = z.object({
  title: z.string().trim().min(1, 'Title is required').max(200, 'At most 200 characters'),
  description: z.string().max(2000, 'At most 2000 characters'),
  dueDate: z.string(), // yyyy-mm-dd or empty
  isCompleted: z.boolean(),
})
type Values = z.infer<typeof schema>

const toDateInput = (iso: string | null) => (iso ? iso.slice(0, 10) : '')
const fromDateInput = (v: string) => (v ? new Date(`${v}T00:00:00`).toISOString() : null)

/** Create (no `todo`) or edit (with `todo`) dialog. The owner is never part of the form. */
export function TodoFormDialog({
  todo,
  trigger,
  onSaved,
}: {
  todo?: Todo
  trigger: ReactElement
  onSaved: (saved: Todo) => void
}) {
  const [open, setOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    reset,
    setValue,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      title: todo?.title ?? '',
      description: todo?.description ?? '',
      dueDate: toDateInput(todo?.dueDate ?? null),
      isCompleted: todo?.isCompleted ?? false,
    },
  })

  const onSubmit = handleSubmit(async (v) => {
    setError(null)
    const input = {
      title: v.title.trim(),
      description: v.description.trim() || null,
      dueDate: fromDateInput(v.dueDate),
      isCompleted: v.isCompleted,
    }
    try {
      const saved = todo
        ? await todoService.update(todo.id, input)
        : await todoService.create({ title: input.title, description: input.description, dueDate: input.dueDate })
      toast.success(todo ? 'Todo updated' : 'Todo created')
      onSaved(saved)
      setOpen(false)
      if (!todo) reset()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Something went wrong')
    }
  })

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger render={trigger} />
      <DialogContent>
        <form onSubmit={onSubmit} noValidate className="grid gap-4">
          <DialogHeader>
            <DialogTitle>{todo ? 'Edit todo' : 'New todo'}</DialogTitle>
            <DialogDescription>{todo ? 'Update the details below.' : 'Add something to your list.'}</DialogDescription>
          </DialogHeader>
          {error && <FormAlert title="Could not save" message={error} />}
          <FieldGroup>
            <Field data-invalid={!!errors.title}>
              <FieldLabel htmlFor="todo-title">Title</FieldLabel>
              <Input id="todo-title" aria-invalid={!!errors.title} {...register('title')} />
              <FieldError errors={[errors.title]} />
            </Field>
            <Field data-invalid={!!errors.description}>
              <FieldLabel htmlFor="todo-description">Description</FieldLabel>
              <Textarea id="todo-description" rows={3} {...register('description')} />
              <FieldError errors={[errors.description]} />
            </Field>
            <Field>
              <FieldLabel htmlFor="todo-due">Due date</FieldLabel>
              <Input id="todo-due" type="date" {...register('dueDate')} />
            </Field>
            {todo && (
              <Field orientation="horizontal">
                <Checkbox
                  id="todo-completed"
                  checked={watch('isCompleted')}
                  onCheckedChange={(c) => setValue('isCompleted', c === true)}
                />
                <FieldLabel htmlFor="todo-completed">Completed</FieldLabel>
              </Field>
            )}
          </FieldGroup>
          <DialogFooter>
            <Button type="submit" disabled={isSubmitting}>
              {isSubmitting && <Spinner />}
              Save
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
