import { createBrowserRouter, Navigate, type RouteObject } from 'react-router'
import { AppLayout } from '@/layouts/AppLayout'
import { AuthLayout } from '@/layouts/AuthLayout'
import {
  AdminUsersPage,
  DashboardPage,
  ForbiddenPage,
  NotFoundPage,
  ProfilePage,
} from '@/pages/AppPages'
import { AdminTodosPage } from '@/pages/AdminTodosPage'
import { TodoDetailPage, TodosPage } from '@/pages/TodoPages'
import { ForgotPasswordPage, LoginPage, RegisterPage, ResetPasswordPage } from '@/pages/AuthPages'
import { GuestRoute } from './GuestRoute'
import { ProtectedRoute } from './ProtectedRoute'

export const routes: RouteObject[] = [
  { path: '/', element: <Navigate to="/dashboard" replace /> },
  {
    element: <GuestRoute />,
    children: [
      {
        element: <AuthLayout />,
        children: [
          { path: '/login', element: <LoginPage /> },
          { path: '/register', element: <RegisterPage /> },
          { path: '/forgot-password', element: <ForgotPasswordPage /> },
          { path: '/reset-password', element: <ResetPasswordPage /> },
        ],
      },
    ],
  },
  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { path: '/dashboard', element: <DashboardPage /> },
          { path: '/profile', element: <ProfilePage /> },
          { path: '/todos', element: <TodosPage /> },
          { path: '/todos/:id', element: <TodoDetailPage /> },
          { path: '/forbidden', element: <ForbiddenPage /> },
          {
            element: <ProtectedRoute role="ADMIN" />,
            children: [
              { path: '/admin/users', element: <AdminUsersPage /> },
              { path: '/admin/todos', element: <AdminTodosPage /> },
            ],
          },
        ],
      },
    ],
  },
  { path: '*', element: <NotFoundPage /> },
]

export const createRouter = () => createBrowserRouter(routes)
