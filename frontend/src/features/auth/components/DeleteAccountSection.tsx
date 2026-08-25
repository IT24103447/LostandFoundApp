import { useState, useRef, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { useAuth } from "../AuthContext";
import { deleteAccount } from "../api/auth";
import { forgotPassword, resetPassword } from "../api/forgotPassword";
import {
  resetPasswordSchema,
  type ResetPasswordFormValues,
} from "../schemas/resetPasswordSchema";
import type { ApiError } from "../../../lib/apiClient";
import { inputClass, isValidationProblem } from "./helpers";
import { Field } from "./Field";
import { PasswordStrengthMeter } from "./PasswordStrengthMeter";

type Mode = "idle" | "send-code" | "reset-code" | "reset-done";

export function DeleteAccountSection() {
  const navigate = useNavigate();
  const { user, logout } = useAuth();

  const [mode, setMode] = useState<Mode>("idle");
  const [sessionToken, setSessionToken] = useState("");
  const [password, setPassword] = useState("");
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [resetSuccess, setResetSuccess] = useState(false);

  const [resetError, setResetError] = useState<string | null>(null);
  const [resendCooldown, setResendCooldown] = useState(0);
  const [isSending, setIsSending] = useState(false);
  const cooldownRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const {
    register,
    handleSubmit,
    setError,
    getValues,
    reset: resetForm,
    formState: { errors, isSubmitting },
  } = useForm<ResetPasswordFormValues>({
    resolver: zodResolver(resetPasswordSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const passwordValue = getValues("newPassword") ?? "";

  useEffect(() => {
    return () => {
      if (cooldownRef.current) clearInterval(cooldownRef.current);
    };
  }, []);

  const handleDelete = async () => {
    setDeleteError(null);
    setIsDeleting(true);
    try {
      await deleteAccount({ password });
      await logout();
      navigate("/login", { replace: true });
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400) {
        const body = apiErr.body as { error?: string } | null;
        setDeleteError(body?.error ?? "Incorrect password.");
      } else if (apiErr.status === 403) {
        const body = apiErr.body as { error?: string } | null;
        setDeleteError(body?.error ?? "Admin accounts cannot be deleted.");
      } else {
        setDeleteError("Something went wrong. Please try again.");
      }
    } finally {
      setIsDeleting(false);
    }
  };

  const handleSendCode = async () => {
    setResetError(null);
    setIsSending(true);
    try {
      const result = await forgotPassword({ email: user!.email });
      if (result.sessionToken) {
        setSessionToken(result.sessionToken);
        setMode("reset-code");
        resetForm();
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
      }
    } catch {
      setResetError("Failed to send code. Please try again.");
    } finally {
      setIsSending(false);
    }
  };

  const handleResend = async () => {
    setResetError(null);
    try {
      const result = await forgotPassword({ email: user!.email });
      if (result.sessionToken) {
        setSessionToken(result.sessionToken);
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
      }
    } catch {
      setResetError("Failed to resend code. Please try again.");
    }
  };

  const handleResetSubmit = async (values: ResetPasswordFormValues) => {
    setResetError(null);
    try {
      await resetPassword({
        sessionToken,
        code: values.code,
        newPassword: values.newPassword,
      });
      if (cooldownRef.current) clearInterval(cooldownRef.current);
      setMode("reset-done");
      setResetSuccess(true);
      setPassword("");
      setTimeout(() => {
        setMode("idle");
        setResetSuccess(false);
      }, 2000);
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
          setResetError(msg);
        } else if (msg.toLowerCase().includes("code")) {
          setError("code", { type: "server", message: msg });
        } else {
          setResetError(msg);
        }
      } else if (apiErr.status === 429) {
        setResetError("Too many attempts. Please wait a minute and try again.");
      } else {
        setResetError("Something went wrong. Please try again.");
      }
    }
  };

  const showError = (name: keyof ResetPasswordFormValues): boolean => {
    if (!errors[name]) return false;
    const value = getValues(name) as string | undefined;
    return !!value && value.trim() !== "";
  };

  // ─── reset-code mode ────────────────────────────────────────────
  if (mode === "reset-code") {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6">
        <h3 className="text-sm font-semibold text-red-800">Reset your password</h3>
        <p className="mt-1 text-sm text-red-700">
          Enter the 6-digit code sent to <strong>{user!.email}</strong> and choose a new password.
        </p>

        <form onSubmit={handleSubmit(handleResetSubmit)} className="mt-4 space-y-4" noValidate>
          <Field
            id="code"
            label="Verification code"
            error={showError("code") ? errors.code?.message : undefined}
          >
            <input
              id="code"
              type="text"
              inputMode="numeric"
              maxLength={6}
              autoComplete="one-time-code"
              {...register("code")}
              className={inputClass(showError("code"))}
            />
          </Field>

          <Field
            id="newPassword"
            label="New password"
            error={showError("newPassword") ? errors.newPassword?.message : undefined}
          >
            <input
              id="newPassword"
              type="password"
              autoComplete="new-password"
              {...register("newPassword")}
              className={inputClass(showError("newPassword"))}
            />
          </Field>

          <PasswordStrengthMeter password={passwordValue} />

          <Field
            id="confirmPassword"
            label="Confirm password"
            error={showError("confirmPassword") ? errors.confirmPassword?.message : undefined}
          >
            <input
              id="confirmPassword"
              type="password"
              autoComplete="new-password"
              {...register("confirmPassword")}
              className={inputClass(showError("confirmPassword"))}
            />
          </Field>

          {resetError && (
            <p className="text-sm text-red-600 font-medium" role="alert">
              {resetError}
            </p>
          )}

          <div className="flex items-center gap-3">
            <button
              type="submit"
              disabled={isSubmitting}
              className="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {isSubmitting ? "Resetting…" : "Reset password"}
            </button>
            <button
              type="button"
              onClick={() => {
                if (cooldownRef.current) clearInterval(cooldownRef.current);
                setMode("send-code");
                setResetError(null);
                resetForm();
              }}
              disabled={isSubmitting}
              className="text-sm font-medium text-red-700 hover:text-red-900 underline transition-colors"
            >
              Back
            </button>
          </div>

          <div className="text-center">
            <button
              type="button"
              onClick={handleResend}
              disabled={resendCooldown > 0 || isSubmitting}
              className="text-xs font-medium text-red-700 hover:text-red-900 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {resendCooldown > 0
                ? `Resend code in ${resendCooldown}s`
                : "Didn't receive the code? Resend"}
            </button>
          </div>
        </form>
      </div>
    );
  }

  // ─── send-code mode ─────────────────────────────────────────────
  if (mode === "send-code") {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6">
        <h3 className="text-sm font-semibold text-red-800">Reset your password</h3>
        <p className="mt-1 text-sm text-red-700">
          We&apos;ll send a reset code to <strong>{user!.email}</strong>.
        </p>

        {resetError && (
          <p className="mt-2 text-sm text-red-600 font-medium" role="alert">
            {resetError}
          </p>
        )}

        <div className="mt-4 flex items-center gap-3">
          <button
            onClick={handleSendCode}
            disabled={isSending}
            className="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSending ? "Sending…" : "Send code"}
          </button>
          <button
            onClick={() => {
              setMode("idle");
              setResetError(null);
            }}
            disabled={isSending}
            className="text-sm font-medium text-red-700 hover:text-red-900 underline transition-colors"
          >
            Back
          </button>
        </div>
      </div>
    );
  }

  // ─── idle / reset-done mode ─────────────────────────────────────
  return (
    <div className="rounded-lg border border-red-200 bg-red-50 p-6">
      <h3 className="text-sm font-semibold text-red-800">Delete account</h3>
      <p className="mt-1 text-sm text-red-700">
        Permanently delete your account and all associated data. This action
        cannot be undone.
      </p>

      {resetSuccess && (
        <div className="mt-3 rounded-md bg-emerald-50 border border-emerald-200 p-3">
          <p className="text-sm text-emerald-700 font-medium">
            Password reset successfully! Enter your new password to continue.
          </p>
        </div>
      )}

      <div className="mt-4">
        <label htmlFor="delete-password" className="block text-sm font-medium text-red-800">
          Confirm your password
        </label>
        <input
          id="delete-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => {
            setPassword(e.target.value);
            setDeleteError(null);
          }}
          placeholder="Enter your password"
          className={`${inputClass(!!deleteError)} mt-1`}
        />
        <button
          type="button"
          onClick={() => setMode("send-code")}
          className="mt-1 text-xs font-medium text-red-700 hover:text-red-900 underline transition-colors"
        >
          Forgot your password?
        </button>
      </div>

      {deleteError && (
        <p className="mt-2 text-sm text-red-600 font-medium" role="alert">
          {deleteError}
        </p>
      )}

      <div className="mt-4">
        <button
          onClick={handleDelete}
          disabled={isDeleting || !password}
          className="rounded-lg border border-red-300 bg-white px-4 py-2 text-sm font-medium text-red-700 shadow-sm hover:bg-red-50 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
        >
          {isDeleting ? "Deleting…" : "Delete account"}
        </button>
      </div>
    </div>
  );
}
