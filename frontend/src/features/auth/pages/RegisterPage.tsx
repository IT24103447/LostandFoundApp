import { RegisterForm } from "../components/RegisterForm";

export function RegisterPage() {
  return (
    <main className="flex min-h-screen items-center justify-center bg-gray-50 px-4 py-12">
      <div className="w-full max-w-md rounded-lg border border-gray-200 bg-white p-8 shadow-sm">
        <h1 className="mb-6 text-center text-2xl font-semibold text-gray-900">
          Create your account
      </h1>
        <RegisterForm />
    </div>
  </main>
  );
}
