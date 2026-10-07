import { Navigate, Outlet } from "react-router-dom";
import { useAuth } from "../AuthContext";

export function ProtectedRoute({
  roles,
  usersOnly,
}: {
  roles?: string[];
  usersOnly?: boolean;
}) {
  const { isAuthenticated, isLoading, user } = useAuth();

  if (isLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent" />
      </div>
    );
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />;
  }

  if (roles?.length && !roles.some((r) => r === "Admin" && user?.isAdmin)) {
    return <Navigate to="/" replace />;
  }

  if (usersOnly && user?.isAdmin) {
    return <Navigate to="/admin/dashboard" replace />;
  }

  return <Outlet />;
}
