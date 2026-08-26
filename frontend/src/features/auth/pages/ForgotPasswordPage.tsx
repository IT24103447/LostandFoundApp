import { ForgotPasswordForm } from "../components/ForgotPasswordForm";
import { useNavigate, Navigate } from "react-router-dom";
import { useAuth } from "../AuthContext";

export function ForgotPasswordPage() {
  const navigate = useNavigate();
  const { isAuthenticated, isLoading, user } = useAuth();

  if (isLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <div className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent" />
      </div>
    );
  }

  if (isAuthenticated) {
    return <Navigate to={user?.isAdmin ? "/admin/dashboard" : "/"} replace />;
  }

  return (
    <main className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-50 via-white to-purple-50 px-4 py-12 sm:px-6 lg:px-8 relative overflow-hidden">
      <div className="absolute top-[-10%] left-[-10%] w-96 h-96 rounded-full bg-indigo-300 opacity-20 blur-[100px] pointer-events-none" />
      <div className="absolute bottom-[-10%] right-[-10%] w-96 h-96 rounded-full bg-purple-300 opacity-20 blur-[100px] pointer-events-none" />

      <div className="w-full max-w-md rounded-2xl border border-white/40 bg-white/70 p-8 shadow-2xl backdrop-blur-xl relative z-10 transition-all duration-300">
        <div className="text-center mb-8">
          <h1 className="text-3xl font-extrabold tracking-tight text-gray-900 mb-2">
            Forgot password?
          </h1>
          <p className="text-sm text-gray-500">
            Enter your email and we'll send you a reset code.
          </p>
        </div>

        <ForgotPasswordForm />

        <p className="mt-6 text-center text-sm text-gray-600">
          Remember your password?{" "}
          <button
            onClick={() => navigate("/login")}
            className="font-medium text-indigo-600 hover:text-indigo-500 transition-colors"
          >
            Sign in
          </button>
        </p>
      </div>
    </main>
  );
}
