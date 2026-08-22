import { useState } from "react";
import { useNavigate } from "react-router-dom";

export function VerifyEmailPage() {
  const [code, setCode] = useState("");
  const [message, setMessage] = useState("");
  const navigate = useNavigate();

  return (
    <main className="flex min-h-screen items-center justify-center bg-gray-50 px-4 py-12">
      <div className="w-full max-w-md rounded-lg border border-gray-200 bg-white p-8 shadow-sm">
        <h1 className="mb-6 text-center text-2xl font-semibold text-gray-900">
          Verify your email
        </h1>
        <p className="mb-4 text-sm text-gray-600">
          Enter the 6-digit code sent to your email.
        </p>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            setMessage("Verification submitted (placeholder).");
            // navigate("/"); // redirect on success
          }}
          className="space-y-4"
        >
          <input
            type="text"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            placeholder="Enter code"
            className="w-full rounded-md border border-gray-300 px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <button
            type="submit"
            className="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700"
          >
            Verify
          </button>
        </form>
        <button
          onClick={() => setMessage("Resend clicked (placeholder).")}
          className="mt-4 w-full text-sm text-blue-600 hover:underline"
        >
          Resend code
        </button>
        {message && (
          <p className="mt-4 text-sm text-green-700">{message}</p>
        )}
      </div>
    </main>
  );
}
