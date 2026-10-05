import { zodResolver } from '@hookform/resolvers/zod'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'
import { FormAlert } from '@/components/FormAlert'
import { Button } from '@/components/ui/button'
import { Field, FieldDescription, FieldError, FieldGroup, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { ApiError } from '@/lib/api'
import * as authService from '@/services/auth.service'
import { registerSchema, type RegisterValues } from './schemas'

export function RegisterForm() {
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)
  const {
    register,
    handleSubmit,
    setError: setFieldError,
    formState: { errors, isSubmitting },
  } = useForm<RegisterValues>({ resolver: zodResolver(registerSchema) })

  const onSubmit = handleSubmit(async (values) => {
    setError(null)
    try {
      await authService.register({
        email: values.email,
        password: values.password,
        firstName: values.firstName,
        lastName: values.lastName,
      })
      toast.success('Account created. You can sign in now.')
      navigate('/login', { replace: true })
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setFieldError('email', { message: 'An account with this email already exists' })
      } else if (err instanceof ApiError && err.status === 400 && err.errors.length > 0) {
        err.errors.forEach((e) => setFieldError(e.field as keyof RegisterValues, { message: e.message }))
      } else if (err instanceof ApiError && err.status === 429) {
        setError('Too many attempts. Please wait a few minutes and try again.')
      } else {
        setError(err instanceof ApiError ? err.message : 'Something went wrong. Please try again.')
      }
    }
  })

  return (
    <form onSubmit={onSubmit} noValidate className="grid gap-6">
      {error && <FormAlert title="Registration failed" message={error} />}
      <FieldGroup>
        <div className="grid grid-cols-2 gap-4">
          <Field data-invalid={!!errors.firstName}>
            <FieldLabel htmlFor="firstName">First name</FieldLabel>
            <Input id="firstName" autoComplete="given-name" aria-invalid={!!errors.firstName} {...register('firstName')} />
            <FieldError errors={[errors.firstName]} />
          </Field>
          <Field data-invalid={!!errors.lastName}>
            <FieldLabel htmlFor="lastName">Last name</FieldLabel>
            <Input id="lastName" autoComplete="family-name" aria-invalid={!!errors.lastName} {...register('lastName')} />
            <FieldError errors={[errors.lastName]} />
          </Field>
        </div>
        <Field data-invalid={!!errors.email}>
          <FieldLabel htmlFor="email">Email</FieldLabel>
          <Input id="email" type="email" autoComplete="email" aria-invalid={!!errors.email} {...register('email')} />
          <FieldError errors={[errors.email]} />
        </Field>
        <Field data-invalid={!!errors.password}>
          <FieldLabel htmlFor="password">Password</FieldLabel>
          <Input
            id="password"
            type="password"
            autoComplete="new-password"
            aria-invalid={!!errors.password}
            {...register('password')}
          />
          <FieldDescription>At least 12 characters, mixing three of: lowercase, uppercase, digits, symbols.</FieldDescription>
          <FieldError errors={[errors.password]} />
        </Field>
        <Field data-invalid={!!errors.confirmPassword}>
          <FieldLabel htmlFor="confirmPassword">Confirm password</FieldLabel>
          <Input
            id="confirmPassword"
            type="password"
            autoComplete="new-password"
            aria-invalid={!!errors.confirmPassword}
            {...register('confirmPassword')}
          />
          <FieldError errors={[errors.confirmPassword]} />
        </Field>
      </FieldGroup>
      <Button type="submit" disabled={isSubmitting}>
        {isSubmitting && <Spinner />}
        Create account
      </Button>
    </form>
  )
}
