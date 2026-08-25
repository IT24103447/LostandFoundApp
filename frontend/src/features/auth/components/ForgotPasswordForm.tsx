import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  forgotPasswordSchema,
  type ForgotPasswordFormValues,
} from "../schemas/forgotPasswordSchema";
import { forgotPassword } from "../api/forgotPassword";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "./Field";
import { inputClass } from "./helpers";

export function ForgotPasswordForm() {
  const navigate = useNavigate();
  const {
    register: registerField,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<ForgotPasswordFormValues>({
    resolver: zodResolver(forgotPasswordSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const [submitError, setSubmitError] = useState<string | null>(null);

  const onSubmit = async (values: ForgotPasswordFormValues) => {
    setSubmitError(null);

    try {
      const result = await forgotPassword(values);
      if (result.sessionToken) {
        navigate("/reset-password", {
          state: { sessionToken: result.sessionToken, email: values.email },
        });
      } else {
        setSubmitError(
          "If an account with that email exists, a reset code has been sent.",
        );
      }
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 429) {
        setSubmitError("Too many attempts. Please wait a minute and try again.");
      } else if (apiErr.status === 400) {
        const body = apiErr.body as { error?: string } | null;
        setSubmitError(body?.error ?? "Something went wrong. Please try again.");
      } else {
        setSubmitError("Something went wrong. Please try again.");
      }
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
            Sending code…
          </>
        ) : (
          "Send reset code"
        )}
      </button>

      {submitError && (
        <p className="text-sm text-red-500 font-medium animate-fade-in" role="alert">
          {submitError}
        </p>
      )}
    </form>
  );
}
