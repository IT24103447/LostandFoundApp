import { useEffect, useState, useRef } from "react";
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

  // OTP state: Array of 6 strings
  const [codeDigits, setCodeDigits] = useState<string[]>(Array(6).fill(""));
  const inputRefs = useRef<(HTMLInputElement | null)[]>([]);

  const [email, setEmail] = useState(initialEmail);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [isVerifying, setIsVerifying] = useState(false);
  const [isResending, setIsResending] = useState(false);
  const [resendCooldown, setResendCooldown] = useState(0);

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
          setResendCooldown(0);
          setTimeout(() => navigate("/"), 1000);
          return;
        }

      } catch {
        // ignore polling errors
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

  useEffect(() => {
    if (resendCooldown <= 0) return;
    const id = setInterval(() => {
      setResendCooldown((c) => {
        if (c <= 1) {
          clearInterval(id);
          return 0;
        }
        return c - 1;
      });
    }, 1000);
    return () => clearInterval(id);
  }, [resendCooldown]);

  if (!sessionToken) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-50 via-white to-purple-50 px-4 py-12">
        <div className="w-full max-w-md rounded-2xl border border-white/20 bg-white/60 p-8 shadow-xl backdrop-blur-xl text-center">
          <h1 className="mb-4 text-2xl font-bold text-gray-900">Session Expired</h1>
          <p className="mb-6 text-sm text-gray-600">Please register again to receive a new verification code.</p>
          <button
            onClick={() => navigate("/")}
            className="w-full rounded-xl bg-gradient-to-r from-indigo-600 to-purple-600 px-4 py-3 text-sm font-semibold text-white shadow-lg transition-all hover:from-indigo-700 hover:to-purple-700 hover:shadow-indigo-500/25 active:scale-[0.98]"
          >
            Go to Register
          </button>
        </div>
      </main>
    );
  }

  const doVerify = async (codeString: string) => {
    if (codeString.length !== 6) return;
    setError("");
    setMessage("");
    setIsVerifying(true);
    try {
      const res = await verifyEmail({ sessionToken, code: codeString });
      if (res.verified) {
        try { 
          sessionStorage.removeItem("verificationSessionToken"); 
          sessionStorage.removeItem("verificationEmail"); 
        } catch {}
        setMessage(`Email ${res.email} verified. Redirecting...`);
        setTimeout(() => navigate("/"), 1200);
      }
    } catch (err) {
      const apiErr = err as ApiError;
      const body = apiErr.body as { error?: string } | null;
      if (apiErr.status === 429) setError("Too many attempts. Please wait and try again.");
      else setError(body?.error ?? "Invalid or expired verification code.");
      
      // Clear code digits on error
      setCodeDigits(Array(6).fill(""));
      inputRefs.current[0]?.focus();
    } finally {
      setIsVerifying(false);
    }
  };

  const handleChange = (index: number, val: string) => {
    // allow only numbers
    const value = val.replace(/[^0-9]/g, "");
    if (!value && val !== "") return; // if user typed non-number, ignore

    const newDigits = [...codeDigits];

    if (value.length > 1) {
      // Handle paste scenario
      const chars = value.split("").slice(0, 6);
      chars.forEach((char, i) => {
        if (index + i < 6) newDigits[index + i] = char;
      });
      setCodeDigits(newDigits);
      
      const nextFocus = Math.min(index + chars.length, 5);
      inputRefs.current[nextFocus]?.focus();

      const combined = newDigits.join("");
      if (combined.length === 6) {
        doVerify(combined);
      }
      return;
    }

    newDigits[index] = value;
    setCodeDigits(newDigits);

    if (value && index < 5) {
      inputRefs.current[index + 1]?.focus();
    }
    
    const combined = newDigits.join("");
    if (combined.length === 6 && value) {
      doVerify(combined);
    }
  };

  const handleKeyDown = (index: number, e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Backspace" && !codeDigits[index] && index > 0) {
      inputRefs.current[index - 1]?.focus();
    }
  };

  const handleVerifySubmit = (e: React.FormEvent) => {
    e.preventDefault();
    doVerify(codeDigits.join(""));
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
      setResendCooldown(60);
      setCodeDigits(Array(6).fill(""));
      inputRefs.current[0]?.focus();
    } catch (err) {
      const apiErr = err as ApiError;
      const body = apiErr.body as { error?: string } | null;
      if (apiErr.status === 429) {
        const retry = (apiErr.body as { error?: string } | null)?.error ?? "Please wait before requesting another code.";
        setError(retry);
        const match = retry.match(/(\d+)\s+seconds/);
        if (match) {
          setResendCooldown(Number(match[1]));
        }
      } else {
        setError(body?.error ?? "Failed to resend code. Please try again.");
      }
    } finally {
      setIsResending(false);
    }
  };

  return (
    <main className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-50 via-white to-purple-50 px-4 py-12 sm:px-6 lg:px-8 relative overflow-hidden">
      {/* Decorative background elements */}
      <div className="absolute top-[-10%] left-[-10%] w-96 h-96 rounded-full bg-indigo-300 opacity-20 blur-[100px] pointer-events-none" />
      <div className="absolute bottom-[-10%] right-[-10%] w-96 h-96 rounded-full bg-purple-300 opacity-20 blur-[100px] pointer-events-none" />

      <div className="w-full max-w-md rounded-2xl border border-white/40 bg-white/70 p-8 shadow-2xl backdrop-blur-xl relative z-10 transition-all duration-300">
        <div className="text-center mb-8">
          <h1 className="text-3xl font-extrabold tracking-tight text-gray-900 mb-2">Verify your email</h1>
          <p className="text-sm text-gray-500">
            We've sent a 6-digit verification code to your email.
          </p>
        </div>



        <form onSubmit={handleVerifySubmit} className="space-y-6" noValidate>
          <div className="flex justify-between gap-2 sm:gap-3">
            {codeDigits.map((digit, idx) => (
              <input
                key={idx}
                ref={(el) => { inputRefs.current[idx] = el; }}
                type="text"
                inputMode="numeric"
                maxLength={6}
                value={digit}
                onChange={(e) => handleChange(idx, e.target.value)}
                onKeyDown={(e) => handleKeyDown(idx, e)}
                disabled={isVerifying}
                className="w-12 h-14 sm:w-14 sm:h-16 text-center text-2xl font-bold text-gray-900 bg-white/50 border border-gray-200 rounded-xl shadow-sm focus:bg-white focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200 focus:outline-none transition-all disabled:opacity-50 disabled:cursor-not-allowed"
              />
            ))}
          </div>

          <button
            type="submit"
            disabled={isVerifying || codeDigits.join("").length < 6}
            className="w-full flex justify-center items-center rounded-xl bg-gradient-to-r from-indigo-600 to-purple-600 px-4 py-3.5 text-sm font-semibold text-white shadow-lg transition-all hover:from-indigo-700 hover:to-purple-700 hover:shadow-indigo-500/25 active:scale-[0.98] disabled:opacity-60 disabled:cursor-not-allowed disabled:hover:scale-100 disabled:hover:shadow-none"
          >
            {isVerifying ? (
              <>
                <svg className="animate-spin -ml-1 mr-3 h-5 w-5 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
                Verifying...
              </>
            ) : (
              "Verify Code"
            )}
          </button>
        </form>

        <div className="mt-8 pt-6 border-t border-gray-200/60">
          <label htmlFor="resend-email" className="mb-2 block text-sm font-medium text-gray-700">
            Didn't receive the code?
          </label>
          <div className="flex gap-3">
            <input
              id="resend-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="you@example.com"
              className="flex-1 rounded-xl border border-gray-200 bg-white/60 px-4 py-2.5 text-sm text-gray-900 focus:bg-white focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200 focus:outline-none transition-all"
            />
            <button
              type="button"
              onClick={handleResend}
              disabled={isResending || resendCooldown > 0}
              className="px-4 py-2.5 rounded-xl border border-gray-200 bg-white text-sm font-medium text-gray-700 hover:bg-gray-50 hover:text-indigo-600 focus:outline-none focus:ring-2 focus:ring-indigo-200 transition-all shadow-sm disabled:opacity-50 disabled:cursor-not-allowed whitespace-nowrap"
            >
              {isResending ? "Sending..." : resendCooldown > 0 ? `Resend ${resendCooldown}s` : "Resend"}
            </button>
          </div>
        </div>

        <div className="mt-6 min-h-[1.5rem] text-center">
          {message && (
            <p className="text-sm font-medium text-emerald-600 animate-fade-in" role="status">
              {message}
            </p>
          )}
          {error && (
            <p className="text-sm font-medium text-red-500 animate-fade-in" role="alert">
              {error}
            </p>
          )}
        </div>
      </div>
    </main>
  );
}
