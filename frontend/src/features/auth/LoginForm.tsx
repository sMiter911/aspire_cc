import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { Link, useLocation, useNavigate } from 'react-router'
import { FormAlert } from '@/components/FormAlert'
import { Button } from '@/components/ui/button'
import { Field, FieldError, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { useAuth } from '@/hooks/useAuth'
import { ApiError } from '@/lib/api'
import { loginSchema, type LoginValues } from './schemas'

interface LocationState {
  from?: string
  expired?: boolean
}

function messageFor(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 401) return 'Invalid email or password.'
    if (err.status === 429) return 'Too many attempts. Please wait a moment and try again.'
    return err.message
  }
  return 'Something went wrong. Please try again.'
}

export function LoginForm() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const state = (useLocation().state ?? {}) as LocationState
  const [error, setError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<LoginValues>({ resolver: zodResolver(loginSchema) })

  const onSubmit = handleSubmit(async (values) => {
    setError(null)
    try {
      await login(values)
      navigate(state.from ?? '/dashboard', { replace: true })
    } catch (err) {
      setError(messageFor(err))
    }
  })

  return (
    <form onSubmit={onSubmit} noValidate className="grid gap-6">
      {state.expired && <FormAlert title="Session expired" message="For your security, please sign in again." />}
      {error && <FormAlert title="Sign-in failed" message={error} />}
      <FieldGroup>
        <Field data-invalid={!!errors.email}>
          <FieldLabel htmlFor="email">Email</FieldLabel>
          <Input id="email" type="email" autoComplete="username" aria-invalid={!!errors.email} {...register('email')} />
          <FieldError errors={[errors.email]} />
        </Field>
        <Field data-invalid={!!errors.password}>
          <div className="flex items-center justify-between">
            <FieldLabel htmlFor="password">Password</FieldLabel>
            <Link to="/forgot-password" className="text-xs text-muted-foreground underline-offset-4 hover:underline">
              Forgot password?
            </Link>
          </div>
          <Input
            id="password"
            type="password"
            autoComplete="current-password"
            aria-invalid={!!errors.password}
            {...register('password')}
          />
          <FieldError errors={[errors.password]} />
        </Field>
      </FieldGroup>
      <Button type="submit" disabled={isSubmitting}>
        {isSubmitting && <Spinner />}
        Sign in
      </Button>
    </form>
  )
}
