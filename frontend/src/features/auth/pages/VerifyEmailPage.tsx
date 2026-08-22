import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  getVerificationStatus,
  resendVerification,
  verifyEmail,
} from "../api/verifyEmail";
import type { ApiError } from "../../../lib/apiClient";

type LocationState = {
  sessionToken?: string;
  email?: string;
};

export function VerifyEmailPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const state = (location.state as LocationState | null) ?? {};
  const storedToken = (() => {
    try { return sessionStorage.getItem("verificationSessionToken") ?? ""; } catch { return ""; }
  })();
  const storedEmail = (() => {
    try { return sessionStorage.getItem("verificationEmail") ?? ""; } catch { return ""; }
  })();
  const sessionToken = state.sessionToken ?? storedToken;
  const initialEmail = state.email ?? storedEmail;

  const [code, setCode] = useState("");
  const [email, setEmail] = useState(initialEmail);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [isVerifying, setIsVerifying] = useState(false);
  const [isResending, setIsResending] = useState(false);
  const [bounced, setBounced] = useState(false);
  const [codeError, setCodeError] = useState("");

  useEffect(() => {
    if (state.sessionToken) {
      try {
        sessionStorage.setItem("verificationSessionToken", state.sessionToken);
        if (state.email) sessionStorage.setItem("verificationEmail", state.email);
      } catch {}
    }
  }, [state.sessionToken, state.email]);

  useEffect(() => {
    if (!sessionToken) return;
    let cancelled = false;
    const controller = new AbortController();
    const check = async () => {
      try {
        const status = await getVerificationStatus(sessionToken, controller.signal);
        if (cancelled) return;
        if (status.isEmailVerified) {
          setMessage("Email already verified. Redirecting...");
          setTimeout(() => navigate("/"), 1000);
          return;
        }
        setBounced(!!status.emailBouncedAt || !!status.latestTokenBouncedAt);
      } catch {
        /* ignore polling errors */
      }
    };
    check();
    const id = setInterval(check, 8000);
    return () => {
      cancelled = true;
      controller.abort();
      clearInterval(id);
    };
  }, [sessionToken, navigate]);

  if (!sessionToken) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-gray-50 px-4 py-12">
        <div className="w-full max-w-md rounded-lg border border-gray-200 bg-white p-8 shadow-sm text-center">
          <h1 className="mb-4 text-xl font-semibold text-gray-900">Verification session expired</h1>
          <p className="mb-6 text-sm text-gray-600">Please register again to receive a new verification code.</p>
          <button
            onClick={() => navigate("/")}
            className="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            Go to register
          </button>
        </div>
      </main>
    );
  }

  const validateCode = (v: string) => {
    if (!v.trim()) return "Code is required.";
    if (!/^\d{6}$/.test(v.trim())) return "Code must be 6 digits.";
    return "";
  };

  const handleVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    const ve = validateCode(code);
    setCodeError(ve);
    if (ve) return;
    setError("");
    setMessage("");
    setIsVerifying(true);
    try {
      const res = await verifyEmail({ sessionToken, code: code.trim() });
      if (res.verified) {
        try { sessionStorage.removeItem("verificationSessionToken"); sessionStorage.removeItem("verificationEmail"); } catch {}
        setMessage(`Email ${res.email} verified. Redirecting...`);
        setTimeout(() => navigate("/"), 1200);
      }
    } catch (err) {
      const apiErr = err as ApiError;
      const body = apiErr.body as { error?: string } | null;
      if (apiErr.status === 429) setError("Too many attempts. Please wait and try again.");
      else setError(body?.error ?? "Invalid or expired verification code.");
    } finally {
      setIsVerifying(false);
    }
  };

  const handleResend = async () => {
    setError("");
    setMessage("");
    if (!email.trim()) {
      setError("Please enter your email to resend the code.");
      return;
    }
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) {
      setError("Please enter a valid email address.");
      return;
    }
    setIsResending(true);
    try {
      await resendVerification({ sessionToken, email: email.trim() });
      setMessage(`Verification code resent to ${email.trim()}.`);
    } catch (err) {
      const apiErr = err as ApiError;
      const body = apiErr.body as { error?: string } | null;
      if (apiErr.status === 429) {
        const retry = (apiErr.body as { error?: string } | null)?.error ?? "Please wait before requesting another code.";
        setError(retry);
      } else {
        setError(body?.error ?? "Failed to resend code. Please try again.");
      }
    } finally {
      setIsResending(false);
    }
  };

  return (
    <main className="flex min-h-screen items-center justify-center bg-gray-50 px-4 py-12">
      <div className="w-full max-w-md rounded-lg border border-gray-200 bg-white p-8 shadow-sm">
        <h1 className="mb-6 text-center text-2xl font-semibold text-gray-900">Verify your email</h1>
        <p className="mb-4 text-sm text-gray-600">Enter the 6-digit code sent to your email.</p>
        {bounced && (
          <div className="mb-4 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800" role="alert">
            Your email bounced. Please correct your email address and resend the code.
          </div>
        )}
        <form onSubmit={handleVerify} className="space-y-4" noValidate>
          <div>
            <input
              type="text"
              value={code}
              onChange={(e) => {
                setCode(e.target.value);
                if (codeError) setCodeError(validateCode(e.target.value));
              }}
              placeholder="Enter code"
              inputMode="numeric"
              maxLength={6}
              className={`w-full rounded-md border px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 ${codeError ? "border-red-500" : "border-gray-300"}`}
            />
            {codeError && <p className="mt-1 text-xs text-red-600">{codeError}</p>}
          </div>
          <button
            type="submit"
            disabled={isVerifying}
            className="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 disabled:cursor-not-allowed disabled:opacity-60"
          >
            {isVerifying ? "Verifying..." : "Verify"}
          </button>
        </form>
        <div className="mt-6 border-t border-gray-100 pt-4">
          <label htmlFor="resend-email" className="mb-1 block text-xs font-medium text-gray-700">
            Email for resend {bounced && <span className="text-amber-700">(correct if bounced)</span>}
          </label>
          <input
            id="resend-email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="you@example.com"
            className="w-full rounded-md border border-gray-300 px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
          <button
            onClick={handleResend}
            disabled={isResending}
            className="mt-3 w-full text-sm text-blue-600 hover:underline disabled:opacity-60"
          >
            {isResending ? "Sending..." : "Resend code"}
          </button>
        </div>
        {message && <p className="mt-4 text-sm text-green-700" role="status">{message}</p>}
        {error && <p className="mt-4 text-sm text-red-700" role="alert">{error}</p>}
      </div>
    </main>
  );
}
