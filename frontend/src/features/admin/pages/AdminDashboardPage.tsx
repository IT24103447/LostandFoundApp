import { useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthContext";

export function AdminDashboardPage() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow-sm">
        <div className="mx-auto max-w-7xl px-4 py-4 flex justify-between items-center">
          <h1 className="text-xl font-bold text-indigo-600">back2u - admin</h1>
          <div className="flex items-center gap-4">
            <span className="text-sm text-gray-600">{user?.name}</span>
            <button
              onClick={() => navigate("/admin/profile")}
              className="text-sm font-medium text-indigo-600 hover:text-indigo-500 transition-colors"
            >
              Profile
            </button>
            <button
              onClick={logout}
              className="text-sm font-medium text-red-600 hover:text-red-500 transition-colors"
            >
              Sign out
            </button>
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-7xl px-4 py-12">
        <h2 className="text-3xl font-bold text-gray-900 mb-4">Admin Dashboard</h2>
        <p className="text-gray-500">User management and verification tools coming soon…</p>
      </main>
    </div>
  );
}
