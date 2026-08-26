import { useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthContext";

export function HomePage() {
  const { user, isAuthenticated, logout } = useAuth();
  const navigate = useNavigate();

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow-sm">
        <div className="mx-auto max-w-7xl px-4 py-4 flex justify-between items-center">
          <h1 className="text-xl font-bold text-indigo-600">back2u - lost and found system</h1>
          <div className="flex items-center gap-4">
            {isAuthenticated ? (
              <>
                <span className="text-sm text-gray-600">{user?.name}</span>
                <button
                  onClick={() => navigate("/profile")}
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
              </>
            ) : (
              <>
                <button
                  onClick={() => navigate("/login")}
                  className="text-sm font-medium text-indigo-600 hover:text-indigo-500 transition-colors"
                >
                  Sign in
                </button>
                <button
                  onClick={() => navigate("/register")}
                  className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-700 transition-colors"
                >
                  Register
                </button>
              </>
            )}
          </div>
        </div>
      </header>
      <main className="mx-auto max-w-7xl px-4 py-12">
        <p className="text-gray-500">Item listings coming soon…</p>
      </main>
    </div>
  );
}
