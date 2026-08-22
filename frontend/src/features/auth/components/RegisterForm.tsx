import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  registerSchema,
  type RegisterFormValues,
} from "../schemas/registerSchema";
import { register } from "../api/register";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "./Field";
import { inputClass, isValidationProblem } from "./helpers";
import { PasswordStrengthMeter } from "./PasswordStrengthMeter";

export function RegisterForm() {
  const navigate = useNavigate();
  const {
    register: registerField,
    handleSubmit,
    setError,
    reset,
    getValues,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<RegisterFormValues>({
    resolver: zodResolver(registerSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const [success, setSuccess] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);

  const showError = (name: keyof RegisterFormValues): boolean => {
    if (isSubmitted) return !!errors[name];
    if (!touchedFields[name]) return false;
    const value = getValues(name) as string | undefined;
    return !!errors[name] && !!value && value.trim() !== "";
  };

  const onSubmit = async (values: RegisterFormValues) => {
    setSuccess(null);
    setSubmitError(null);

    const controller = new AbortController();
    try {
      const { confirmPassword: _confirm, ...payload } = values;
      const result = await register(payload, controller.signal);
      setSuccess(`Account created for ${result.email}. You can now sign in.`);
      try {
        sessionStorage.setItem("verificationSessionToken", result.verificationSessionToken);
        sessionStorage.setItem("verificationEmail", result.email);
      } catch {}
      navigate("/verify-email", { state: { sessionToken: result.verificationSessionToken, email: result.email } });
      reset({
        name: "",
        email: "",
        phoneNo: "",
        password: "",
        confirmPassword: "",
      });
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400 && isValidationProblem(apiErr.body)) {
        for (const [field, messages] of Object.entries(apiErr.body.errors) as Array<
          [string, string[]]
        >) {
          const lcField = field.charAt(0).toLowerCase() + field.slice(1);
          setError(lcField as keyof RegisterFormValues, {
            type: "server",
            message: messages.join(" "),
          });
        }
      } else if (apiErr.status === 409) {
        const body = apiErr.body as { error?: string } | null;
        const msg = body?.error ?? "An account with these details already exists.";
        if (msg.toLowerCase().includes("email")) {
          setError("email", { type: "server", message: msg });
        } else if (msg.toLowerCase().includes("phone")) {
          setError("phoneNo", { type: "server", message: msg });
        } else {
          setSubmitError(msg);
        }
      } else if (apiErr.status === 429) {
        setSubmitError("Too many attempts. Please wait a minute and try again.");
      } else {
        setSubmitError("Something went wrong. Please try again.");
      }
    }
  };

  const passwordValue = getValues("password") ?? "";

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-5" noValidate>
      <Field
        id="name"
        label="Full name"
        error={showError("name") ? errors.name?.message : undefined}
      >
        <input
          id="name"
          type="text"
          autoComplete="name"
          {...registerField("name")}
          className={inputClass(showError("name") && !!errors.name)}
        />
      </Field>

      <Field
        id="email"
        label="Email"
        error={showError("email") ? errors.email?.message : undefined}
      >
        <input
          id="email"
          type="email"
          autoComplete="email"
          {...registerField("email")}
          className={inputClass(showError("email") && !!errors.email)}
        />
      </Field>

      <Field
        id="phoneNo"
        label="Phone number"
        hint="e.g. +94771234567"
        error={showError("phoneNo") ? errors.phoneNo?.message : undefined}
      >
        <input
          id="phoneNo"
          type="text"
          inputMode="tel"
          autoComplete="tel"
          {...registerField("phoneNo")}
          className={inputClass(showError("phoneNo") && !!errors.phoneNo)}
        />
      </Field>

      <Field
        id="password"
        label="Password"
        hint="at least one upper case letter, lower case letter, and one digit must be included."
        error={showError("password") ? errors.password?.message : undefined}
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
          id="password"
          type={showPassword ? "text" : "password"}
          autoComplete="new-password"
          {...registerField("password")}
          className={inputClass(showError("password") && !!errors.password)}
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
            Creating account…
          </>
        ) : (
          "Create account"
        )}
      </button>

      {success && (
        <p className="text-sm text-emerald-600 font-medium animate-fade-in" role="status">
          {success}
        </p>
      )}
      {submitError && (
        <p className="text-sm text-red-500 font-medium animate-fade-in" role="alert">
          {submitError}
        </p>
      )}
    </form>
  );
}
