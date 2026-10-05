import { Link } from 'react-router'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { buttonVariants } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { LoginForm } from '@/features/auth/LoginForm'
import { RegisterForm } from '@/features/auth/RegisterForm'
import { useAuth } from '@/hooks/useAuth'
import { cn } from '@/lib/utils'

export function LoginPage() {
  const { error } = useAuth()
  return (
    <Card>
      <CardHeader>
        <CardTitle>Sign in</CardTitle>
        <CardDescription>Welcome back. Enter your credentials to continue.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {error && (
          <Alert variant="destructive">
            <AlertTitle>Connection problem</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <LoginForm />
      </CardContent>
      <CardFooter className="justify-center text-sm text-muted-foreground">
        No account?&nbsp;
        <Link to="/register" className="text-foreground underline-offset-4 hover:underline">
          Create one
        </Link>
      </CardFooter>
    </Card>
  )
}

export function RegisterPage() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Create an account</CardTitle>
        <CardDescription>Register to access your dashboard.</CardDescription>
      </CardHeader>
      <CardContent>
        <RegisterForm />
      </CardContent>
      <CardFooter className="justify-center text-sm text-muted-foreground">
        Already registered?&nbsp;
        <Link to="/login" className="text-foreground underline-offset-4 hover:underline">
          Sign in
        </Link>
      </CardFooter>
    </Card>
  )
}

/** Password reset needs an outbound email channel, which this starter does not include yet. */
function NotAvailableYet({ title }: { title: string }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        <CardDescription>This flow is not available yet.</CardDescription>
      </CardHeader>
      <CardContent>
        <Alert>
          <AlertTitle>Needs an email provider</AlertTitle>
          <AlertDescription>
            Password reset requires sending a single-use link by email. Wire up a mail service and the reset-token
            endpoints in the API (see the README roadmap) and this screen will be implemented.
          </AlertDescription>
        </Alert>
      </CardContent>
      <CardFooter>
        <Link to="/login" className={cn(buttonVariants({ variant: 'outline' }), 'w-full')}>
          Back to sign in
        </Link>
      </CardFooter>
    </Card>
  )
}

export const ForgotPasswordPage = () => <NotAvailableYet title="Forgot password" />
export const ResetPasswordPage = () => <NotAvailableYet title="Reset password" />
