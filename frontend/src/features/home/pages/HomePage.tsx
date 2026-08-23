import { useAuth } from "../../auth/AuthContext";

export function HomePage() {
  const { user, logout } = useAuth();

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow-sm">
        <div className="mx-auto max-w-7xl px-4 py-4 flex justify-between items-center">
          <h1 className="text-xl font-bold text-indigo-600">back2u - lost and found system</h1>
          <div className="flex items-center gap-4">
            <span className="text-sm text-gray-600">{user?.name}</span>
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
        <p className="text-gray-500">Item listings coming soon…</p>
      </main>
    </div>
  );
}
