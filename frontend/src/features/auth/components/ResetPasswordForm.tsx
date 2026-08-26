import { useState, useRef, useEffect } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  resetPasswordSchema,
  type ResetPasswordFormValues,
} from "../schemas/resetPasswordSchema";
import { resetPassword, forgotPassword } from "../api/forgotPassword";
import { useAuth } from "../AuthContext";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "./Field";
import { inputClass, isValidationProblem } from "./helpers";
import { PasswordStrengthMeter } from "./PasswordStrengthMeter";

type LocationState = {
  sessionToken?: string;
  email?: string;
};

export function ResetPasswordForm() {
  const navigate = useNavigate();
  const location = useLocation();
  const { refreshUser } = useAuth();
  const state = (location.state as LocationState | null) ?? {};

  const sessionToken = state.sessionToken ?? "";
  const email = state.email ?? "";

  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);
  const [resendCooldown, setResendCooldown] = useState(0);
  const [isResending, setIsResending] = useState(false);
  const cooldownRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    if (!success) return;
    const timer = setTimeout(() => navigate("/"), 3000);
    return () => clearTimeout(timer);
  }, [success, navigate]);

  const {
    register: registerField,
    handleSubmit,
    setError,
    getValues,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<ResetPasswordFormValues>({
    resolver: zodResolver(resetPasswordSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const showError = (name: keyof ResetPasswordFormValues): boolean => {
    if (isSubmitted) return !!errors[name];
    if (!touchedFields[name]) return false;
    const value = getValues(name) as string | undefined;
    return !!errors[name] && !!value && value.trim() !== "";
  };

  const passwordValue = getValues("newPassword") ?? "";

  const onSubmit = async (values: ResetPasswordFormValues) => {
    setSubmitError(null);

    if (!sessionToken) {
      setSubmitError("Session expired. Please request a new reset code.");
      return;
    }

    try {
      await resetPassword({
        sessionToken,
        code: values.code,
        newPassword: values.newPassword,
      });
      await refreshUser();
      setSuccess(true);
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400 && isValidationProblem(apiErr.body)) {
        for (const [field, messages] of Object.entries(apiErr.body.errors) as Array<
          [string, string[]]
        >) {
          const lcField = field.charAt(0).toLowerCase() + field.slice(1);
          setError(lcField as keyof ResetPasswordFormValues, {
            type: "server",
            message: messages.join(" "),
          });
        }
      } else if (apiErr.status === 400) {
        const body = apiErr.body as { error?: string } | null;
        const msg = body?.error ?? "Reset failed.";
        if (msg.toLowerCase().includes("too many")) {
          setSubmitError(msg);
        } else if (msg.toLowerCase().includes("code")) {
          setError("code", { type: "server", message: msg });
        } else {
          setSubmitError(msg);
        }
      } else if (apiErr.status === 429) {
        setSubmitError("Too many attempts. Please wait a minute and try again.");
      } else if (apiErr.status === 403) {
        const body = apiErr.body as { error?: string; verificationSessionToken?: string; email?: string } | null;
        if (body?.verificationSessionToken) {
          navigate("/verify-email", {
            state: { sessionToken: body.verificationSessionToken, email: body.email },
            replace: true,
          });
          return;
        }
        setSubmitError(body?.error ?? "Something went wrong.");
      } else {
        setSubmitError("Something went wrong. Please try again.");
      }
    }
  };

  const handleResend = async () => {
    if (!email) {
      setSubmitError("No email available. Please go back and try again.");
      return;
    }
    setIsResending(true);
    setSubmitError(null);
    try {
      const result = await forgotPassword({ email });
      if (result.sessionToken) {
        navigate("/reset-password", {
          state: { sessionToken: result.sessionToken, email },
          replace: true,
        });
        setResendCooldown(60);
        if (cooldownRef.current) clearInterval(cooldownRef.current);
        cooldownRef.current = setInterval(() => {
          setResendCooldown((c) => {
            if (c <= 1) {
              if (cooldownRef.current) clearInterval(cooldownRef.current);
              return 0;
            }
            return c - 1;
          });
        }, 1000);
      } else {
        navigate("/forgot-password");
      }
    } catch {
      setSubmitError("Failed to resend code. Please try again.");
    } finally {
      setIsResending(false);
    }
  };

  if (!sessionToken) {
    return (
      <div className="text-center">
        <p className="text-sm text-gray-600 mb-4">
          Your reset session has expired.
        </p>
        <button
          onClick={() => navigate("/forgot-password")}
          className="font-medium text-indigo-600 hover:text-indigo-500 transition-colors text-sm"
        >
          Request a new code
        </button>
      </div>
    );
  }

  if (success) {
    return (
      <div className="text-center space-y-4">
        <p className="text-sm text-emerald-600 font-medium">
          Password reset successfully!
        </p>
        <p className="text-sm text-gray-500">
          Redirecting to home…
        </p>
        <button
          onClick={() => navigate("/")}
          className="w-full rounded-xl bg-gradient-to-r from-indigo-600 to-purple-600 px-4 py-3 text-sm font-semibold text-white shadow-lg transition-all hover:from-indigo-700 hover:to-purple-700 hover:shadow-indigo-500/25 active:scale-[0.98]"
        >
          Go to home
        </button>
      </div>
    );
  }

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-5" noValidate>
      <Field
        id="code"
        label="Verification code"
        hint="6-digit code sent to your email"
        error={showError("code") ? errors.code?.message : undefined}
      >
        <input
          id="code"
          type="text"
          inputMode="numeric"
          maxLength={6}
          autoComplete="one-time-code"
          {...registerField("code")}
          className={inputClass(showError("code") && !!errors.code)}
        />
      </Field>

      <Field
        id="newPassword"
        label="New password"
        hint="at least one upper case letter, lower case letter, and one digit must be included."
        error={showError("newPassword") ? errors.newPassword?.message : undefined}
        trailing={
          <button
            type="button"
            onClick={() => setShowPassword((v) => !v)}
            className="text-gray-500 hover:text-gray-700 focus:outline-none focus:text-gray-700"
            aria-label={showPassword ? "Hide password" : "Show password"}
          >
            {showPassword ? (
              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M3.98 8.223A10.477 10.477 0 001.934 12C3.226 16.338 7.244 19.5 12 19.5c1.99 0 3.85-.625 5.388-1.698M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M3 3l18 18" />
              </svg>
            ) : (
              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M2.036 12.322a1.012 1.012 0 010-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
            )}
          </button>
        }
      >
        <input
          id="newPassword"
          type={showPassword ? "text" : "password"}
          autoComplete="new-password"
          {...registerField("newPassword")}
          className={inputClass(showError("newPassword") && !!errors.newPassword)}
        />
      </Field>

      <PasswordStrengthMeter password={passwordValue} />

      <Field
        id="confirmPassword"
        label="Confirm password"
        error={showError("confirmPassword") ? errors.confirmPassword?.message : undefined}
        trailing={
          <button
            type="button"
            onClick={() => setShowConfirmPassword((v) => !v)}
            className="text-gray-500 hover:text-gray-700 focus:outline-none focus:text-gray-700"
            aria-label={showConfirmPassword ? "Hide password" : "Show password"}
          >
            {showConfirmPassword ? (
              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M3.98 8.223A10.477 10.477 0 001.934 12C3.226 16.338 7.244 19.5 12 19.5c1.99 0 3.85-.625 5.388-1.698M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M3 3l18 18" />
              </svg>
            ) : (
              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" d="M2.036 12.322a1.012 1.012 0 010-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178z" />
                <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
            )}
          </button>
        }
      >
        <input
          id="confirmPassword"
          type={showConfirmPassword ? "text" : "password"}
          autoComplete="new-password"
          {...registerField("confirmPassword")}
          className={inputClass(showError("confirmPassword") && !!errors.confirmPassword)}
        />
      </Field>

      <button
        type="submit"
        disabled={isSubmitting}
        className="w-full flex justify-center items-center rounded-xl bg-gradient-to-r from-indigo-600 to-purple-600 px-4 py-3 text-sm font-semibold text-white shadow-lg transition-all hover:from-indigo-700 hover:to-purple-700 hover:shadow-indigo-500/25 active:scale-[0.98] disabled:opacity-60 disabled:cursor-not-allowed disabled:hover:scale-100 disabled:hover:shadow-none"
      >
        {isSubmitting ? (
          <>
            <svg className="animate-spin -ml-1 mr-3 h-5 w-5 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
            </svg>
            Resetting…
          </>
        ) : (
          "Reset password"
        )}
      </button>

      {submitError && (
        <p className="text-sm text-red-500 font-medium animate-fade-in" role="alert">
          {submitError}
        </p>
      )}

      <div className="text-center">
        <button
          type="button"
          onClick={handleResend}
          disabled={isResending || resendCooldown > 0}
          className="text-sm font-medium text-indigo-600 hover:text-indigo-500 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {isResending
            ? "Sending…"
            : resendCooldown > 0
              ? `Resend code in ${resendCooldown}s`
              : "Didn't receive the code? Resend"}
        </button>
      </div>
    </form>
  );
}
