import { Routes, Route, Navigate } from "react-router-dom";
import { RegisterPage } from "./features/auth/pages/RegisterPage";
import { VerifyEmailPage } from "./features/auth/pages/VerifyEmailPage";
import { LoginPage } from "./features/auth/pages/LoginPage";
import { HomePage } from "./features/home/pages/HomePage";
import { AdminDashboardPage } from "./features/admin/pages/AdminDashboardPage";
import { ProtectedRoute } from "./features/auth/components/ProtectedRoute";
import { useAuth } from "./features/auth/AuthContext";

function RootRedirect() {
  const { isAuthenticated, isLoading, user } = useAuth();

  if (isLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent" />
      </div>
    );
  }

  if (isAuthenticated && user?.isAdmin) {
    return <Navigate to="/admin/dashboard" replace />;
  }
  if (isAuthenticated) {
    return <Navigate to="/home" replace />;
  }
  return <RegisterPage />;
}

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<RootRedirect />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />

      <Route element={<ProtectedRoute />}>
        <Route path="/home" element={<HomePage />} />
      </Route>

      <Route element={<ProtectedRoute roles={["Admin"]} />}>
        <Route path="/admin/dashboard" element={<AdminDashboardPage />} />
      </Route>
    </Routes>
  );
}
