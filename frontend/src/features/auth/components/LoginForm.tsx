import { useState } from "react";
import { useNavigate, useLocation } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { loginSchema, type LoginFormValues } from "../schemas/loginSchema";
import { useAuth } from "../AuthContext";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "./Field";
import { inputClass } from "./helpers";

export function LoginForm() {
  const navigate = useNavigate();
  const location = useLocation();
  const { login } = useAuth();
  const [error, setError] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);

  const {
    register: registerField,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<LoginFormValues>({
    resolver: zodResolver(loginSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const onSubmit = async (values: LoginFormValues) => {
    setError(null);
    try {
      const profile = await login(values.email, values.password);
      const from = (location.state as { from?: string } | null)?.from;
      if (profile.isAdmin) {
        navigate("/admin/dashboard", { replace: true });
      } else {
        navigate(from || "/", { replace: true });
      }
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 403) {
        const body = apiErr.body as { error?: string; email?: string; verificationSessionToken?: string } | null;
        if (body?.verificationSessionToken) {
          navigate("/verify-email", {
            state: { sessionToken: body.verificationSessionToken, email: body.email ?? values.email },
          });
          return;
        }
      }
      const body = apiErr.body as { error?: string } | null;
      setError(body?.error ?? "Login failed. Please try again.");
    }
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-5" noValidate>
      <Field
        id="email"
        label="Email"
        error={errors.email?.message}
      >
        <input
          id="email"
          type="email"
          autoComplete="email"
          {...registerField("email")}
          className={inputClass(!!errors.email)}
        />
      </Field>

      <Field
        id="password"
        label="Password"
        error={errors.password?.message}
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
          autoComplete="current-password"
          {...registerField("password")}
          className={inputClass(!!errors.password)}
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
            Signing in…
          </>
        ) : (
          "Sign in"
        )}
      </button>

      {error && (
        <p className="text-sm text-red-500 font-medium animate-fade-in" role="alert">
          {error}
        </p>
      )}
    </form>
  );
}
