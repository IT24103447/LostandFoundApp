import { RegisterForm } from "../components/RegisterForm";
import { useNavigate } from "react-router-dom";

export function RegisterPage() {
  const navigate = useNavigate();

  return (
    <main className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-50 via-white to-purple-50 px-4 py-12 sm:px-6 lg:px-8 relative overflow-hidden">
      {/* Decorative background elements */}
      <div className="absolute top-[-10%] left-[-10%] w-96 h-96 rounded-full bg-indigo-300 opacity-20 blur-[100px] pointer-events-none" />
      <div className="absolute bottom-[-10%] right-[-10%] w-96 h-96 rounded-full bg-purple-300 opacity-20 blur-[100px] pointer-events-none" />

      <div className="w-full max-w-md rounded-2xl border border-white/40 bg-white/70 p-8 shadow-2xl backdrop-blur-xl relative z-10 transition-all duration-300">
        <div className="text-center mb-8">
          <h1 className="text-3xl font-extrabold tracking-tight text-gray-900 mb-2">
            Create your account
          </h1>
          <p className="text-sm text-gray-500">
            Get started with your free account
          </p>
        </div>

        <RegisterForm />

        <div className="mt-6 text-center">
          <button
            type="button"
            onClick={() => navigate(-1)}
            className="text-sm font-medium text-indigo-600 hover:text-indigo-500 transition-colors"
          >
            ← Back
          </button>
        </div>
      </div>
    </main>
  );
}
