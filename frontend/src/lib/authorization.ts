/**
 * The ONE place the UI decides what a user may see. Components never compare role strings themselves; they ask
 * `useAuthorization()` or use <RequireRole> / <RequirePermission> / <ProtectedRoute>.
 *
 * This is UX only. Hiding a button or route protects nothing: the Spring Boot and ASP.NET Core APIs enforce every
 * rule again on each request, and a USER who types /admin/todos still gets 403 from the API.
 */
export type Role = 'USER' | 'ADMIN'

export type Capability =
  | 'todos:manage-own' // create / read / update / delete own todos
  | 'todos:read-all' // list every user's todos
  | 'todos:manage-all' // update / delete any todo
  | 'todos:release' // manually release a waiting todo / release all
  | 'todos:retry' // re-queue a failed todo
  | 'users:read' // list users (Spring admin endpoint)

const roleCapabilities: Record<Role, readonly Capability[]> = {
  USER: ['todos:manage-own'],
  ADMIN: ['todos:manage-own', 'todos:read-all', 'todos:manage-all', 'todos:release', 'todos:retry', 'users:read'],
}

export function capabilitiesFor(roles: readonly string[]): ReadonlySet<Capability> {
  const caps = new Set<Capability>()
  for (const role of roles) {
    roleCapabilities[role as Role]?.forEach((c) => caps.add(c))
  }
  return caps
}
